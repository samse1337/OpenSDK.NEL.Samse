using System.Text.Json;
using Codexus.Cipher.Entities;
using Codexus.Cipher.Entities.WPFLauncher.NetGame;
using Codexus.Cipher.Protocol;
using Codexus.Cipher.Protocol.Registers;
using Codexus.Development.SDK.Entities;
using Codexus.Development.SDK.Manager;
using Codexus.Development.SDK.Utils;
using Codexus.Game.Launcher.Services.Java;
using Codexus.Game.Launcher.Utils;
using Codexus.Interceptors;
using Codexus.OpenSDK;
using Codexus.OpenSDK.Entities.X19;
using Codexus.OpenSDK.Entities.Yggdrasil;
using Codexus.OpenSDK.Generator;
using Codexus.OpenSDK.Http;
using Codexus.OpenSDK.Yggdrasil;
using OpenSDK.NEL.Samse;
using OpenSDK.NEL.Samse.Entities;
using Serilog;
using Spectre.Console;
using System.Collections.Generic;
using System.IO;

namespace OpenSDK.NEL.Samse
{
    internal class Program
    {
        private const string AccountsFile = "accounts.json";
        private static List<SavedAccount> SavedAccounts = new();
        private static Services? _services;
        private static X19AuthenticationOtp? _authOtp;
        private static string? _channel;

        static async Task Main(string[] args)
        {
            ConfigureLogger();
            LoadSavedAccounts();

            AnsiConsole.MarkupLine("[bold aquamarine3]* 此软件基于 Codexus.OpenSDK 以及 Codexus.Development.SDK 制作，旨在为您提供更简洁的脱盒体验。[/]");
            AnsiConsole.WriteLine();

            await InitializeSystemComponentsAsync();
            _services = await CreateServices();
            await _services.X19.InitializeDeviceAsync();

            await LoginOrSelectAccountAsync();
            await MainLoopAsync();
        }

        static void LoadSavedAccounts()
        {
            if (!File.Exists(AccountsFile)) return;

            try
            {
                var json = File.ReadAllText(AccountsFile);
                SavedAccounts = JsonSerializer.Deserialize<List<SavedAccount>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                Log.Information("已加载 {Count} 个历史账号", SavedAccounts.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载账号文件失败");
            }
        }

        static void SaveAccounts()
        {
            try
            {
                var json = JsonSerializer.Serialize(SavedAccounts, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(AccountsFile, json);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存账号失败");
            }
        }

        static void AddOrUpdateAccount(string name, string type, string token, string? cookie, string entityId, string channel)
        {
            var existing = SavedAccounts.Find(a => a.EntityId == entityId);
            if (existing != null)
            {
                existing.Name = name;
                existing.Token = token;
                existing.Cookie = cookie;
                existing.LastUsed = DateTime.UtcNow;
                SaveAccounts();
                return;
            }

            SavedAccounts.Add(new SavedAccount
            {
                Name = name,
                Type = type,
                Token = token,
                Cookie = cookie,
                EntityId = entityId,
                Channel = channel,
                LastUsed = DateTime.UtcNow
            });

            SavedAccounts = SavedAccounts.OrderByDescending(a => a.LastUsed).ToList();
            SaveAccounts();
        }

        static async Task LoginOrSelectAccountAsync()
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();

                var choices = new List<string> { "新登录（Cookie / 4399）" };
                foreach (var acc in SavedAccounts.Take(10))
                {
                    var last = acc.LastUsed.ToLocalTime().ToString("MM-dd HH:mm");
                    choices.Add($"[dim]{last}[/] {acc.Name} ({acc.EntityId})");
                }
                choices.Add("退出程序");

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold yellow]选择登录方式[/]")
                        .AddChoices(choices)
                );

                if (choice.Contains("新登录"))
                {
                    await NewLoginAsync();
                    return;
                }
                else if (choice.Contains("退出"))
                {
                    AnsiConsole.MarkupLine("[red]再见！[/]");
                    Environment.Exit(0);
                }
                else
                {
                    var selected = SavedAccounts[choices.IndexOf(choice) - 1];
                    if (await TryLoginWithSavedAccount(selected))
                        return;
                }
            }
        }

        static async Task NewLoginAsync()
        {
            AnsiConsole.Clear();
            DisplayHeader();

            var mode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold yellow]新登录方式[/]")
                    .AddChoices("1. Cookie 登录", "2. 随机 4399 登录")
            );

            if (mode.Contains("1"))
                await LoginWithCookieAsync(true);
            else
                await LoginWith4399Async(true);
        }

        static async Task<bool> TryLoginWithSavedAccount(SavedAccount acc)
        {
            var success = await AnsiConsole.Status()
                .StartAsync($"正在使用历史账号登录：{acc.Name}...", async ctx =>
                {
                    try
                    {
                        var (auth, channel) = acc.Type == "Cookie"
                            ? await _services!.X19.ContinueAsync(acc.Cookie!)
                            : await _services!.X19.ContinueAsync(acc.Token!);

                        _authOtp = auth;
                        _channel = channel;

                        acc.LastUsed = DateTime.UtcNow;
                        SaveAccounts();

                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "历史账号登录失败");
                        return false;
                    }
                });

            if (success)
            {
                AnsiConsole.MarkupLine($"[green]历史账号登录成功！[/] {acc.Name}");
                Utilities.WaitForContinue();
                return true;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]登录失败（可能已过期）[/]");
                var delete = AnsiConsole.Confirm("[yellow]是否删除此账号？[/]");
                if (delete)
                {
                    SavedAccounts.Remove(acc);
                    SaveAccounts();
                    AnsiConsole.MarkupLine("[red]已删除[/]");
                }
                Utilities.WaitForContinue();
                return false;
            }
        }

        static async Task LoginWithCookieAsync(bool save = false)
        {
            string? accountName = null;
            if (save)
            {
                accountName = AnsiConsole.Prompt(
                    new TextPrompt<string>("[dim]为账号命名（便于下次识别）:[/]")
                        .DefaultValue("Cookie用户")
                        .AllowEmpty()
                );
                if (string.IsNullOrWhiteSpace(accountName))
                    accountName = "Cookie用户";
            }

            var cookie = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]请输入 Cookie:[/]")
                    .AllowEmpty()
            );

            if (string.IsNullOrWhiteSpace(cookie))
            {
                AnsiConsole.MarkupLine("[red]Cookie 不能为空[/]");
                Utilities.WaitForContinue();
                return;
            }

            var success = await AnsiConsole.Status()
                .StartAsync("登录中...", async ctx =>
                {
                    try
                    {
                        var result = await _services!.X19.ContinueAsync(cookie);
                        _authOtp = result.Item1;
                        _channel = result.Item2;

                        if (save && accountName != null)
                        {
                            AddOrUpdateAccount(accountName, "Cookie", "", cookie, _authOtp.EntityId, _channel);
                        }

                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Cookie 登录失败");
                        return false;
                    }
                });

            if (success)
            {
                AnsiConsole.MarkupLine($"[green]登录成功！[/] 用户ID: [bold]{_authOtp!.EntityId}[/] 渠道: [bold]{_channel}[/]");
                Utilities.WaitForContinue();
            }
            else
            {
                AnsiConsole.MarkupLine("[red]登录失败[/]");
                Utilities.WaitForContinue();
            }
        }

        static async Task LoginWith4399Async(bool save = false)
        {
            string? accountName = null;
            if (save)
            {
                accountName = AnsiConsole.Prompt(
                    new TextPrompt<string>("[dim]为账号命名（便于下次识别）:[/]")
                        .DefaultValue("4399-随机账号")
                        .AllowEmpty()
                );
                if (string.IsNullOrWhiteSpace(accountName))
                    accountName = "4399-随机账号";
            }

            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                var success = await AnsiConsole.Status()
                    .StartAsync($"注册 4399 账号... (第 {attempt}/{maxRetries} 次)", async ctx =>
                    {
                        try
                        {
                            var user = await _services!.Register.RegisterAsync(
                                _services.Api.ComputeCaptchaAsync,
                                () => new IdCard
                                {
                                    Name = Channel4399Register.GenerateChineseName(),
                                    IdNumber = Channel4399Register.GenerateRandomIdCard()
                                });

                            var json = await _services.C4399.LoginWithPasswordAsync(user.Account, user.Password);
                            var result = await _services.X19.ContinueAsync(json);

                            _authOtp = result.Item1;
                            _channel = result.Item2;

                            if (save && accountName != null)
                            {
                                AddOrUpdateAccount(accountName, "4399", json, null, _authOtp.EntityId, _channel);
                            }

                            return true;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "注册失败");
                            return false;
                        }
                    });

                if (success)
                {
                    AnsiConsole.MarkupLine($"[green]4399 登录成功！[/] 用户ID: [bold]{_authOtp!.EntityId}[/] 渠道: [bold]{_channel}[/]");
                    Utilities.WaitForContinue();
                    return;
                }

                if (attempt < maxRetries)
                    await Task.Delay(2000);
            }

            AnsiConsole.MarkupLine("[red]所有重试均失败[/]");
            Utilities.WaitForContinue();
        }

        static async Task MainLoopAsync()
        {
            while (true)
            {
                var selectedServer = await SelectServerAsync();
                await ManageServerAsync(selectedServer);
            }
        }

        static async Task<EntityNetGameItem> SelectServerAsync()
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                AnsiConsole.MarkupLine($"[bold cyan]当前账号：[/][bold]{_authOtp!.EntityId}[/] ({_channel})");

                var keyword = AnsiConsole.Prompt(
                    new TextPrompt<string>("搜索服务器关键字:")
                        .PromptStyle("yellow")
                        .AllowEmpty()
                );

                var servers = await _authOtp.Api<EntityNetGameKeyword, Entities<EntityNetGameItem>>(
                    "/item/query/search-by-keyword",
                    new EntityNetGameKeyword { Keyword = keyword });

                if (servers.Data.Length == 0)
                {
                    AnsiConsole.MarkupLine("[red]未找到服务器[/]");
                    Utilities.WaitForContinue();
                    continue;
                }

                var table = new Table()
                    .AddColumn("编号")
                    .AddColumn("服务器名称")
                    .AddColumn("ID")
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Grey);

                for (int i = 0; i < servers.Data.Length; i++)
                {
                    table.AddRow(
                        (i + 1).ToString(),
                        Markup.Escape(servers.Data[i].Name),
                        servers.Data[i].EntityId
                    );
                }

                AnsiConsole.Write(table);
                var choice = PromptNumberInRange(1, servers.Data.Length, "请选择服务器");
                return servers.Data[choice - 1];
            }
        }

        static async Task ManageServerAsync(EntityNetGameItem server)
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                AnsiConsole.MarkupLine($"[bold cyan]服务器：[/][bold]{server.Name}[/] (ID: {server.EntityId})");

                var roles = await GetServerRolesAsync(server);
                DisplayRoles(roles);

                var operation = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold yellow]操作[/]")
                        .AddChoices("1. 启动代理", "2. 随机角色", "3. 指定角色", "4. 返回")
                );

                switch (operation[0])
                {
                    case '1':
                        if (roles.Length == 0) 
                        { 
                            AnsiConsole.Clear(); 
                            DisplayHeader(); 
                            AnsiConsole.MarkupLine("[red]无角色，无法启动代理[/]"); 
                            Utilities.WaitForContinue(); 
                            continue; 
                        }

                        var roleOptions = roles.Select((r, i) => 
                            $"[bold]{i + 1}[/]. {Markup.Escape(r.Name)} (ID: {r.GameId})"
                        ).ToArray();

                        var selectedRoleText = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("[bold yellow]请选择要使用的角色[/]")
                                .PageSize(10)
                                .MoreChoicesText("[grey]（使用 ↑↓ 键移动）[/]")
                                .AddChoices(roleOptions)
                        );

                        var selectedIndex = Array.IndexOf(roleOptions, selectedRoleText);
                        await StartProxyAsync(server, roles[selectedIndex]);
                        return;

                    case '2': 
                        await CreateRandomCharacterAsync(server); 
                        break;
                    case '3': 
                        await CreateNamedCharacterAsync(server); 
                        break;
                    case '4': 
                        return;
                }
            }
        }

        static async Task<EntityGameCharacter[]> GetServerRolesAsync(EntityNetGameItem server)
        {
            var roles = await _authOtp!.Api<EntityQueryGameCharacters, Entities<EntityGameCharacter>>(
                "/game-character/query/user-game-characters",
                new EntityQueryGameCharacters
                {
                    GameId = server.EntityId,
                    UserId = _authOtp.EntityId
                });
            return roles.Data;
        }

        static void DisplayRoles(EntityGameCharacter[] roles)
        {
            if (roles.Length > 0)
            {
                var table = new Table()
                    .AddColumn("编号")
                    .AddColumn("角色名")
                    .AddColumn("角色ID")
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Grey);

                for (int i = 0; i < roles.Length; i++)
                {
                    table.AddRow(
                        (i + 1).ToString(),
                        Markup.Escape(roles[i].Name),
                        roles[i].GameId
                    );
                }
                AnsiConsole.Write(table);
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]暂无角色[/]");
            }
        }

        static async Task CreateRandomCharacterAsync(EntityNetGameItem server)
        {
            var name = StringGenerator.GenerateRandomString(12, false);
            await CreateCharacterAsync(server, name);
            AnsiConsole.MarkupLine($"[green]已创建随机角色：[/][bold]{name}[/]");
            Utilities.WaitForContinue();
        }

        static async Task CreateNamedCharacterAsync(EntityNetGameItem server)
        {
            var name = AnsiConsole.Prompt(
                new TextPrompt<string>("请输入角色名称:")
                    .PromptStyle("yellow")
                    .AllowEmpty()
            );

            if (string.IsNullOrWhiteSpace(name))
            {
                AnsiConsole.MarkupLine("[red]角色名称不能为空[/]");
                Utilities.WaitForContinue();
                return;
            }

            await CreateCharacterAsync(server, name);
            AnsiConsole.MarkupLine($"[green]已创建角色：[/][bold]{name}[/]");
            Utilities.WaitForContinue();
        }

        static async Task CreateCharacterAsync(EntityNetGameItem server, string name)
        {
            try
            {
                await _authOtp!.Api<EntityCreateCharacter, JsonElement>(
                    "/game-character",
                    new EntityCreateCharacter
                    {
                        GameId = server.EntityId,
                        UserId = _authOtp.EntityId,
                        Name = name
                    });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]创建角色失败：[/] {ex.Message}");
            }
        }

        static async Task StartProxyAsync(EntityNetGameItem server, EntityGameCharacter character)
        {
            await AnsiConsole.Status()
                .StartAsync("正在启动本地代理...", async ctx =>
                {
                    try
                    {
                        var details = await _authOtp!.Api<EntityQueryNetGameDetailRequest, Entity<EntityQueryNetGameDetailItem>>(
                            "/item-details/get_v2",
                            new EntityQueryNetGameDetailRequest { ItemId = server.EntityId });

                        var address = await _authOtp.Api<EntityAddressRequest, Entity<EntityNetGameServerAddress>>(
                            "/item-address/get",
                            new EntityAddressRequest { ItemId = server.EntityId });

                        var version = details.Data!.McVersionList[0];
                        var gameVersion = GameVersionUtil.GetEnumFromGameVersion(version.Name);

                        var serverModInfo = await InstallerService.InstallGameMods(
                            _authOtp.EntityId,
                            _authOtp.Token,
                            gameVersion,
                            new WPFLauncher(),
                            server.EntityId,
                            false);

                        var mods = JsonSerializer.Serialize(serverModInfo);

                        CreateProxyInterceptor(server, character, version, address.Data!, mods);

                        await X19.InterconnectionApi.GameStartAsync(_authOtp.EntityId, _authOtp.Token, server.EntityId);

                        AnsiConsole.MarkupLine($"[green]代理已成功启动！[/]");
                        AnsiConsole.MarkupLine($"[bold]角色：[/]{character.Name}");
                        AnsiConsole.MarkupLine("[grey]按任意键关闭代理并返回...[/]");
                        Console.ReadKey(true);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]启动代理失败：[/] {ex.Message}");
                        Utilities.WaitForContinue();
                    }
                });
        }

        static void CreateProxyInterceptor(
            EntityNetGameItem server,
            EntityGameCharacter character,
            EntityMcVersion version,
            EntityNetGameServerAddress address,
            string mods)
        {
            Interceptor.CreateInterceptor(
                new EntitySocks5 { Enabled = false },
                mods,
                server.EntityId,
                server.Name,
                version.Name,
                address.Ip,
                address.Port,
                character.Name,
                _authOtp!.EntityId,
                _authOtp.Token,
                YggdrasilCallback);

            void YggdrasilCallback(string serverId)
            {
                Log.Information("Server ID: {Certification}", serverId);
                var pair = Md5Mapping.GetMd5FromGameVersion(version.Name);

                var signal = new SemaphoreSlim(0);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var success = await _services!.Yggdrasil.JoinServerAsync(new GameProfile
                        {
                            GameId = server.EntityId,
                            GameVersion = version.Name,
                            BootstrapMd5 = pair.BootstrapMd5,
                            DatFileMd5 = pair.DatFileMd5,
                            Mods = JsonSerializer.Deserialize<ModList>(mods)!,
                            User = new UserProfile { UserId = int.Parse(_authOtp.EntityId), UserToken = _authOtp.Token }
                        }, serverId);

                        if (success.IsSuccess)
                            Log.Information("消息认证成功");
                        else
                            Log.Error("消息认证失败: {Error}", success.Error);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "认证异常");
                    }
                    finally
                    {
                        signal.Release();
                    }
                });

                signal.Wait();
            }
        }

        static void ConfigureLogger()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        static async Task InitializeSystemComponentsAsync()
        {
            Interceptor.EnsureLoaded();
            PacketManager.Instance.EnsureRegistered();
            PluginManager.Instance.EnsureUninstall();
            PluginManager.Instance.LoadPlugins("plugins");
            await Task.CompletedTask;
        }

        static async Task<Services> CreateServices()
        {
            var api = new WebNexusApi("YXBwSWQ9Q29kZXh1cy5HYXRld2F5LmFwcFNlY3JldD1hN0s5bTJYcUw4YkM0d1ox");
            var register = new Channel4399Register();
            var c4399 = new C4399();
            var x19 = new X19();

            var yggdrasil = new StandardYggdrasil(new YggdrasilData
            {
                LauncherVersion = x19.GameVersion,
                Channel = "netease",
                CrcSalt = await ComputeCrcSalt()
            });

            return new Services(api, register, c4399, x19, yggdrasil);
        }

        static async Task<string> ComputeCrcSalt()
        {
            Log.Information("正在计算 CRC Salt...");
            var http = new HttpWrapper("https://service.codexus.today",
                options => options.WithBearerToken("0e9327a2-d0f8-41d5-8e23-233de1824b9a.pk_053ff2d53503434bb42fe158"));
            var response = await http.GetAsync("/crc-salt");
            var json = await response.Content.ReadAsStringAsync();
            var entity = JsonSerializer.Deserialize<OpenSdkResponse<CrcSalt>>(json);
            return entity?.Data.Salt ?? string.Empty;
        }

        static void DisplayHeader()
        {
            AnsiConsole.Write(
                new FigletText("OpenSDK.NEL")
                    .Centered()
                    .Color(Color.Aquamarine3)
            );
            AnsiConsole.MarkupLine("[cornflowerblue] 此软件基于 Codexus.OpenSDK 以及 Codexus.Development.SDK 制作，旨在为您提供更简洁的脱盒体验。[/]");
            AnsiConsole.Write(new Rule().RuleStyle("grey").Centered());
            AnsiConsole.WriteLine();
        }

        static int PromptNumberInRange(int min, int max, string prompt)
        {
            AnsiConsole.MarkupLine($"[dim]{prompt}（范围：[bold]{min}-{max}[/]）：[/]");
            return AnsiConsole.Prompt(
                new TextPrompt<int>("")
                    .HideDefaultValue()
                    .PromptStyle("yellow")
                    .ValidationErrorMessage($"[red]请输入 {min}-{max} 的数字[/]")
                    .Validate(n => n >= min && n <= max)
            );
        }
    }

    internal record SavedAccount
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Token { get; set; } = "";
        public string? Cookie { get; set; }
        public string EntityId { get; set; } = "";
        public string Channel { get; set; } = "";
        public DateTime LastUsed { get; set; }
    }

    internal record Services(
        WebNexusApi Api,
        Channel4399Register Register,
        C4399 C4399,
        X19 X19,
        StandardYggdrasil Yggdrasil
    );

    public static class Utilities
    {
        public static void WaitForContinue()
        {
            AnsiConsole.MarkupLine("\n[grey]按任意键继续...[/]");
            Console.ReadKey(true);
        }
    }
}