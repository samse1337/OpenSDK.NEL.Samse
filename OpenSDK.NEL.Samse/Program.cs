using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using Codexus.OpenSDK.Entities.MPay;
using Codexus.OpenSDK.Entities.X19;
using Codexus.OpenSDK.Entities.Yggdrasil;
using Codexus.OpenSDK.Exceptions;
using Codexus.OpenSDK.Generator;
using Codexus.OpenSDK.Http;
using Codexus.OpenSDK.Yggdrasil;
using OpenSDK.NEL.Samse.Entities;
using Serilog;
using Spectre.Console;
using Spectre.Console.Rendering;

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
                SavedAccounts = JsonSerializer.Deserialize<List<SavedAccount>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch { }
        }

        static void SaveAccounts()
        {
            try
            {
                var json = JsonSerializer.Serialize(SavedAccounts, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(AccountsFile, json);
            }
            catch { }
        }

        static void AddOrUpdateAccount(string name, string type, string token, string? cookie, string entityId, string channel)
        {
            var existing = SavedAccounts.Find(a => a.EntityId == entityId);
            if (existing != null)
            {
                existing.Name = name;
                existing.Type = type;
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

                var choices = new List<string>
                {
                    "随机 4399 登录",
                    "Cookie 登录",
                    "网易账号登录",
                    "4399 登录",
                    "删除历史账号"
                };

                foreach (var acc in SavedAccounts.Take(10))
                {
                    var last = acc.LastUsed.ToLocalTime().ToString("MM-dd HH:mm");
                    var typeName = acc.Type switch
                    {
                        "Cookie" => "Cookie",
                        "4399" => "4399账号",
                        "X19" => "x19账号",
                        _ => acc.Type
                    };
                    choices.Add($"[dim]{last}[/] {acc.Name} - [bold]{typeName}[/]");
                }

                choices.Add("退出程序");

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold yellow]请选择操作[/]")
                        .PageSize(15)
                        .MoreChoicesText("[grey]（使用 ↑↓ 键移动）[/]")
                        .AddChoices(choices));

                if (choice == "退出程序")
                {
                    AnsiConsole.MarkupLine("[red]再见！[/]");
                    Environment.Exit(0);
                }

                if (choice == "Cookie 登录")
                {
                    await LoginWithCookieAsync(true);
                    if (_authOtp != null) return;
                }
                else if (choice == "随机 4399 登录")
                {
                    await LoginWith4399Async(true);
                    if (_authOtp != null) return;
                }
                else if (choice == "4399 登录")
                {
                    await LoginWith4399AccountAsync(true);
                    if (_authOtp != null) return;
                }
                else if (choice == "网易账号登录")
                {
                    await LoginWithX19Async(true);
                    if (_authOtp != null) return;
                }
                else if (choice == "删除历史账号")
                {
                    DeleteSavedAccounts();
                    continue;
                }
                else
                {
                    var index = choices.IndexOf(choice) - 5;
                    if (index >= 0 && index < SavedAccounts.Count)
                    {
                        if (await TryLoginWithSavedAccount(SavedAccounts[index]))
                            return;
                    }
                }
            }
        }
        
        static async Task LoginWith4399AccountAsync(bool save = false)
{
    while (true)
    {
        AnsiConsole.Clear();
        DisplayHeader();

        var accountName = save
            ? AnsiConsole.Prompt(new TextPrompt<string>("[dim]为账号命名（便于识别）:[/]").DefaultValue("4399账号").AllowEmpty()) ?? "4399账号"
            : "4399账号";

        var account = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]请输入 4399 账号:[/]"));
        var password = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]请输入 4399 密码:[/]").Secret());
        string? loginJson = null;

        try
        {
            loginJson = await _services!.C4399.LoginWithPasswordAsync(account, password);
            var result = await _services.X19.ContinueAsync(loginJson);

            _authOtp = result.Item1;
            _channel = result.Item2;
            if (save)
                AddOrUpdateAccount(accountName, "4399", loginJson, null, _authOtp.EntityId, _channel!);

            AnsiConsole.MarkupLine($"[green]4399 登录成功！[/] 用户ID: [bold]{_authOtp.EntityId}[/]");
            Utilities.WaitForContinue();
            return;
        }
        catch (Exception ex) when (ex.Data.Contains("captcha_url") && ex.Data.Contains("session_id"))
        {
            var captchaUrl = ex.Data["captcha_url"]?.ToString();
            var sessionId = ex.Data["session_id"]?.ToString();

            AnsiConsole.MarkupLine("[bold red]需要验证码，已自动打开浏览器[/]");
            AnsiConsole.MarkupLine($"[dim]验证码链接: {captchaUrl}[/]");

            try
            {
                Process.Start(new ProcessStartInfo(captchaUrl!) { UseShellExecute = true });
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]无法自动打开浏览器，请手动打开上方链接[/]");
            }

            var captcha = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]请输入验证码:[/]"));

            try
            {
                loginJson = await _services!.C4399.LoginWithPasswordAsync(account, password, sessionId!, captcha);
                var result = await _services.X19.ContinueAsync(loginJson);

                _authOtp = result.Item1;
                _channel = result.Item2;

                if (save)
                    AddOrUpdateAccount(accountName, "4399", loginJson, null, _authOtp.EntityId, _channel!);

                AnsiConsole.MarkupLine($"[green]4399 登录成功！[/] 用户ID: [bold]{_authOtp.EntityId}[/]");
                Utilities.WaitForContinue();
                return;
            }
            catch (Exception ex2)
            {
                AnsiConsole.MarkupLine($"[red]验证码错误或登录失败：{ex2.Message}[/]");
                if (!AnsiConsole.Confirm("重新尝试登录？")) return;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]登录失败：{ex.Message}[/]");
            if (!AnsiConsole.Confirm("重新输入账号密码？")) return;
        }
    }
}

        static void DeleteSavedAccounts()
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();

                if (SavedAccounts.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]暂无已保存的账号[/]");
                    Utilities.WaitForContinue();
                    return;
                }

                var options = SavedAccounts.ToList();
                options.Add(new SavedAccount { Name = "[返回主菜单]" });

                var selected = AnsiConsole.Prompt(
                    new MultiSelectionPrompt<SavedAccount>()
                        .Title("[bold red]请选择要删除的账号（空格多选，Enter 确认）[/]")
                        .PageSize(15)
                        .MoreChoicesText("[grey]（上下移动，空格选中）[/]")
                        .InstructionsText("[grey]空格多选账号 | [green]Enter[/] 确认删除 | 选中返回项按 Enter 即可返回[/]")
                        .AddChoices(options)
                        .UseConverter(acc =>
                            acc.Name == "[返回主菜单]"
                                ? "[dim grey][[返回主菜单]][/]"
                                : $"{acc.Name} | {(acc.Type == "X19" ? "[green]x19账号[/]" : acc.Type == "4399" ? "[yellow]4399[/]" : "[dim]Cookie[/]")} | ID:{acc.EntityId}")
                );

                if (selected.Count == 0 || selected.All(x => x.Name == "[返回主菜单]"))
                    return;

                var toDelete = selected.Where(x => x.Name != "[返回主菜单]").ToList();
                if (toDelete.Count == 0) return;

                if (AnsiConsole.Confirm($"[red]确认永久删除这 [bold]{toDelete.Count}[/] 个账号吗？[/]"))
                {
                    foreach (var acc in toDelete)
                        SavedAccounts.Remove(acc);
                    SaveAccounts();
                    AnsiConsole.MarkupLine($"[green]已成功删除 {toDelete.Count} 个账号[/]");
                }

                if (!AnsiConsole.Confirm("继续删除其他账号？"))
                    return;
            }
        }

        static async Task LoginWithCookieAsync(bool save = false)
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();

                var accountName = save
                    ? AnsiConsole.Prompt(new TextPrompt<string>("[dim]为账号命名（便于识别）:[/]").DefaultValue("Cookie用户").AllowEmpty()) ?? "Cookie用户"
                    : "Cookie用户";

                var cookie = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]请输入 Cookie:[/]").AllowEmpty());

                if (string.IsNullOrWhiteSpace(cookie))
                {
                    AnsiConsole.MarkupLine("[red]Cookie 不能为空！[/]");
                    if (!AnsiConsole.Confirm("重新输入？")) return;
                    continue;
                }

                var success = await AnsiConsole.Status().StartAsync("Cookie 登录中...", async _ =>
                {
                    try
                    {
                        var result = await _services!.X19.ContinueAsync(cookie);
                        _authOtp = result.Item1;
                        _channel = result.Item2;
                        if (save) AddOrUpdateAccount(accountName, "Cookie", "", cookie, _authOtp.EntityId, _channel);
                        return true;
                    }
                    catch { return false; }
                });

                if (success)
                {
                    AnsiConsole.MarkupLine($"[green]Cookie 登录成功！[/] 用户ID: [bold]{_authOtp!.EntityId}[/]");
                    Utilities.WaitForContinue();
                    return;
                }

                if (!AnsiConsole.Confirm("重新输入 Cookie？")) return;
            }
        }

        static async Task LoginWith4399Async(bool save = false)
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();

                var accountName = save
                    ? AnsiConsole.Prompt(new TextPrompt<string>("[dim]为账号命名（便于识别）:[/]").DefaultValue("4399-随机账号").AllowEmpty()) ?? "4399-随机账号"
                    : "4399-随机账号";

                const int maxRetries = 3;
                bool success = false;

                for (int i = 1; i <= maxRetries && !success; i++)
                {
                    success = await AnsiConsole.Status().StartAsync($"注册 4399 账号... ({i}/{maxRetries})", async _ =>
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
                            if (save) AddOrUpdateAccount(accountName, "4399", json, null, _authOtp.EntityId, _channel);
                            return true;
                        }
                        catch { return false; }
                    });

                    if (!success) await Task.Delay(2000);
                }

                if (success)
                {
                    AnsiConsole.MarkupLine($"[green]4399 登录成功！[/] 用户ID: [bold]{_authOtp!.EntityId}[/]");
                    Utilities.WaitForContinue();
                    return;
                }

                if (!AnsiConsole.Confirm("重新尝试注册？")) return;
            }
        }

        static async Task LoginWithX19Async(bool save = false)
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();

                var accountName = save
                    ? AnsiConsole.Prompt(new TextPrompt<string>("[dim]为账号命名（便于识别）:[/]").DefaultValue("网易账号").AllowEmpty()) ?? "网易账号"
                    : "网易账号";

                var email = AnsiConsole.Ask<string>("[yellow]请输入网易邮箱[/]:");
                var password = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]请输入密码[/]:").Secret());

                var success = await AnsiConsole.Status().StartAsync("正在登录网易账号...", async _ =>
                {
                    try
                    {
                        var mpay = new UniSdkMPay(Projects.DesktopMinecraft, "2.1.0");
                        await mpay.InitializeDeviceAsync();
                        var user = await mpay.LoginWithEmailAsync(email, password);
                        if (user == null) throw new Exception("邮箱或密码错误");

                        var result = await _services!.X19.ContinueAsync(user, mpay.Device);
                        _authOtp = result.Item1;
                        _channel = result.Item2;

                        await X19.InterconnectionApi.LoginStart(_authOtp.EntityId, _authOtp.Token);

                        if (save)
                        {
                            AddOrUpdateAccount(
                                name: accountName,
                                type: "X19",
                                token: Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{password}")),
                                cookie: null,
                                entityId: _authOtp.EntityId,
                                channel: _channel
                            );
                        }
                        return true;
                    }
                    catch { return false; }
                });

                if (success)
                {
                    AnsiConsole.MarkupLine($"[green]网易账号登录成功！[/] ID: [bold]{_authOtp!.EntityId}[/] 渠道: [bold]{_channel}[/]");
                    Utilities.WaitForContinue();
                    return;
                }

                if (!AnsiConsole.Confirm("重新输入账号密码？")) return;
            }
        }

        static async Task<bool> TryLoginWithSavedAccount(SavedAccount acc)
        {
            var success = await AnsiConsole.Status().StartAsync($"正在登录：{acc.Name}...", async _ =>
            {
                try
                {
                    (X19AuthenticationOtp auth, string channel) result = acc.Type switch
                    {
                        "Cookie" => await _services!.X19.ContinueAsync(acc.Cookie!),
                        "4399" or "X19" => await _services!.X19.ContinueAsync(acc.Token!),
                        _ => throw new NotSupportedException()
                    };

                    _authOtp = result.auth;
                    _channel = result.channel;

                    await X19.InterconnectionApi.LoginStart(_authOtp.EntityId, _authOtp.Token);

                    acc.LastUsed = DateTime.UtcNow;
                    SaveAccounts();
                    return true;
                }
                catch { return false; }
            });

            if (success)
            {
                AnsiConsole.MarkupLine($"[green]登录成功！[/] {acc.Name}");
                Utilities.WaitForContinue();
                return true;
            }

            AnsiConsole.MarkupLine("[red]登录失败（可能已过期）[/]");
            if (AnsiConsole.Confirm("是否删除此账号？"))
            {
                SavedAccounts.Remove(acc);
                SaveAccounts();
            }
            Utilities.WaitForContinue();
            return false;
        }

        static async Task MainLoopAsync()
        {
            while (true)
            {
                var server = await SelectServerAsync();
                if (server == null) continue;
                await ManageServerAsync(server);
            }
        }

        static async Task<EntityNetGameItem?> SelectServerAsync()
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                AnsiConsole.MarkupLine($"[bold cyan]当前账号：[bold]{_authOtp!.EntityId}[/] ({_channel})[/]");

                string? keyword;
                try
                {
                    keyword = AnsiConsole.Prompt(
                        new TextPrompt<string>(" [yellow]搜索服务器关键字:[/]")
                            .AllowEmpty()
                            .PromptStyle("yellow"));
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                var servers = await _authOtp.Api<EntityNetGameKeyword, Entities<EntityNetGameItem>>(
                    "/item/query/search-by-keyword",
                    new EntityNetGameKeyword { Keyword = keyword ?? "" });

                if (servers.Data.Length == 0)
                {
                    AnsiConsole.MarkupLine("[red]未找到任何服务器[/]");
                    if (!AnsiConsole.Confirm("重新搜索？[dim]（按 Esc 或取消返回主菜单）[/]"))
                        return null;
                    continue;
                }

                var table = new Table().AddColumn("编号").AddColumn("服务器名称").AddColumn("ID").Border(TableBorder.Rounded).BorderColor(Color.Grey);
                for (int i = 0; i < servers.Data.Length; i++)
                    table.AddRow((i + 1).ToString(), Markup.Escape(servers.Data[i].Name), servers.Data[i].EntityId);
                AnsiConsole.Write(table);

                int choice;
                try
                {
                    choice = PromptNumberInRange(0, servers.Data.Length, "请选择服务器编号");
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (choice == 0) return null;
                return servers.Data[choice - 1];
            }
        }

        static async Task ManageServerAsync(EntityNetGameItem server)
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                AnsiConsole.MarkupLine($"[bold cyan]服务器：[bold]{server.Name}[/] (ID: {server.EntityId})[/]");

                var roles = await GetServerRolesAsync(server);
                DisplayRoles(roles);

                var operation = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold yellow]请选择操作[/]")
                        .AddChoices("启动代理", "创建随机角色", "创建指定角色", "返回服务器列表"));

                if (operation == "返回服务器列表") return;

                switch (operation)
                {
                    case "启动代理":
                        if (roles.Length == 0)
                        {
                            AnsiConsole.MarkupLine("[red]暂无角色，无法启动代理[/]");
                            Utilities.WaitForContinue();
                            continue;
                        }
                        var selectedRole = AnsiConsole.Prompt(
                            new SelectionPrompt<EntityGameCharacter>()
                                .Title("[bold yellow]请选择要使用的角色[/]")
                                .PageSize(10)
                                .AddChoices(roles)
                                .UseConverter(r => $"{r.Name} (ID: {r.GameId})"));
                        await StartProxyAsync(server, selectedRole);
                        return;

                    case "创建随机角色":
                        await CreateRandomCharacterAsync(server);
                        break;
                    case "创建指定角色":
                        await CreateNamedCharacterAsync(server);
                        break;
                }
            }
        }

        static async Task<EntityGameCharacter[]> GetServerRolesAsync(EntityNetGameItem server)
        {
            var result = await _authOtp!.Api<EntityQueryGameCharacters, Entities<EntityGameCharacter>>(
                "/game-character/query/user-game-characters",
                new EntityQueryGameCharacters
                {
                    GameId = server.EntityId,
                    UserId = _authOtp.EntityId
                });
            return result.Data;
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
                    table.AddRow((i + 1).ToString(), Markup.Escape(roles[i].Name), roles[i].GameId);
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
            AnsiConsole.MarkupLine($"[green]已创建随机角色：[bold]{name}[/][/]");
            Utilities.WaitForContinue();
        }

        static async Task CreateNamedCharacterAsync(EntityNetGameItem server)
        {
            var name = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]请输入角色名称:[/]").AllowEmpty());
            if (string.IsNullOrWhiteSpace(name))
            {
                AnsiConsole.MarkupLine("[red]角色名称不能为空[/]");
                Utilities.WaitForContinue();
                return;
            }
            await CreateCharacterAsync(server, name);
            AnsiConsole.MarkupLine($"[green]已创建角色：[bold]{name}[/]");
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
            catch { }
        }

        static async Task StartProxyAsync(EntityNetGameItem server, EntityGameCharacter character)
        {
            AnsiConsole.MarkupLine("[bold yellow]正在启动本地代理...[/]");
            AnsiConsole.MarkupLine($"[bold aqua]Nickname: {character.Name}[/]");

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
                while (true)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    if (key == ConsoleKey.Escape)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]启动失败：[bold]{ex.Message}[/][/]");
                Utilities.WaitForContinue();
            }
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

                        if (!success.IsSuccess)
                            Log.Warning("Yggdrasil 认证失败: {Error}", success.Error);
                    }
                    catch { }
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
            try
            {
                var http = new HttpWrapper("https://service.codexus.today",
                    o => o.WithBearerToken("0e9327a2-d0f8-41d5-8e23-233de1824b9a.pk_053ff2d53503434bb42fe158"));
                var response = await http.GetAsync("/crc-salt");
                var json = await response.Content.ReadAsStringAsync();
                var entity = JsonSerializer.Deserialize<OpenSdkResponse<CrcSalt>>(json);
                return entity?.Data?.Salt ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        static void DisplayHeader()
        {
            AnsiConsole.Write(new FigletText("OpenSDK.NEL").Centered().Color(Color.Aquamarine3));
            AnsiConsole.MarkupLine("[bold aquamarine3]* 此软件基于 Codexus.OpenSDK 以及 Codexus.Development.SDK 制作，旨在为您提供更简洁的脱盒体验。[/]");
            AnsiConsole.Write(new Rule().RuleStyle("grey").Centered());
            AnsiConsole.WriteLine();
        }

        static int PromptNumberInRange(int min, int max, string prompt)
        {
            AnsiConsole.MarkupLine($"[dim]{prompt}[/]");

            while (true)
            {
                try
                {
                    return AnsiConsole.Prompt(
                        new TextPrompt<int>("> ")
                            .PromptStyle("yellow")
                            .ValidationErrorMessage("[red]请输入 0～{0} 之间的数字[/]".Replace("{0}", max.ToString()))
                            .Validate(n => n == 0 || (n >= min && n <= max))
                    );
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }
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