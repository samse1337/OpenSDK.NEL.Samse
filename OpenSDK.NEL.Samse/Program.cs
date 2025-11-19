using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Codexus.Cipher.Entities;
using Codexus.Cipher.Entities.WPFLauncher.NetGame;
using Codexus.Cipher.Entities.WPFLauncher.RentalGame;
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
        private static bool _returnToLogin;
        private static Serilog.ILogger? _originalLogger;

        static async Task Main(string[] args)
        {
            if (args != null && args.Length > 0 && args.Any(a => a.Equals("--log-viewer", StringComparison.OrdinalIgnoreCase)))
            {
                var idx = Array.FindIndex(args, a => a.Equals("--log-viewer", StringComparison.OrdinalIgnoreCase));
                var sid = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : string.Empty;
                RunLogViewer(sid);
                return;
            }
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
                var category = await SelectServerCategoryAsync();
                if (category == null)
                {
                    if (_returnToLogin)
                    {
                        _returnToLogin = false;
                        await LoginOrSelectAccountAsync();
                        continue;
                    }
                    continue;
                }

                if (category == "网络服")
                {
                    var server = await SelectServerAsync();
                    if (server == null)
                    {
                        if (_returnToLogin)
                        {
                            _returnToLogin = false;
                            await LoginOrSelectAccountAsync();
                            continue;
                        }
                        continue;
                    }
                    await ManageServerAsync(server);
                }
                else if (category == "租凭服")
                {
                    AnsiConsole.MarkupLine("[yellow]功能还未实现，请等待更新[/]");
                    Utilities.WaitForContinue();
                    continue;
                }
                else if (category == "管理代理")
                {
                    await ManageProxiesAsync();
                    continue;
                }
            }
        }

        static async Task<string?> SelectServerCategoryAsync()
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                AnsiConsole.MarkupLine($"[bold cyan]当前账号：[bold]{_authOtp!.EntityId}[/] ({_channel})[/]");

                string selected;
                try
                {
                    selected = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold yellow]请选择服务器类型[/]")
                            .AddChoices("网络服", "租凭服", "管理代理", "返回主菜单"));
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (selected == "返回主菜单") { _returnToLogin = true; return null; }
                return selected;
            }
        }

        static async Task<EntityNetGameItem?> SelectServerAsync()
        {
            const int pageSize = 15;
            var offset = 0;
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                AnsiConsole.MarkupLine($"[bold cyan]当前账号：[bold]{_authOtp!.EntityId}[/] ({_channel})[/]");

                Entities<EntityNetGameItem> servers;
                try
                {
                    servers = await _authOtp.Api<EntityNetGameRequest, Entities<EntityNetGameItem>>(
                        "/item/query/available",
                        new EntityNetGameRequest
                        {
                            AvailableMcVersions = Array.Empty<string>(),
                            ItemType = 1,
                            Length = pageSize,
                            Offset = offset,
                            MasterTypeId = "2",
                            SecondaryTypeId = ""
                        });
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (servers.Data.Length == 0)
                {
                    AnsiConsole.MarkupLine("[red]暂无服务器或没有更多内容[/]");
                    var back = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold yellow]请选择操作[/]")
                            .AddChoices(offset > 0 ? new[] { "上一页" } : Array.Empty<string>())
                            .AddChoices(new[] { "返回主菜单" }));
                    if (back == "上一页")
                    {
                        offset = Math.Max(0, offset - pageSize);
                        continue;
                    }
                    _returnToLogin = true;
                    return null;
                }

                var mapping = new Dictionary<string, EntityNetGameItem>();
                var choices = new List<string>();
                for (int i = 0; i < servers.Data.Length; i++)
                {
                    var globalIndex = offset + i + 1;
                    var label = Markup.Escape($"{globalIndex}. {servers.Data[i].Name} (ID: {servers.Data[i].EntityId})");
                    mapping[label] = servers.Data[i];
                    choices.Add(label);
                }
                choices.Insert(0, "搜索服务器");
                if (offset > 0) choices.Add("上一页");
                choices.Add("下一页");
                choices.Add("返回主菜单");

                string selected;
                try
                {
                    selected = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold yellow]请选择服务器或操作[/]")
                            .PageSize(20)
                            .MoreChoicesText("[grey]（上下移动，Enter 选择）[/]")
                            .AddChoices(choices));
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (selected == "返回主菜单") { _returnToLogin = true; return null; }
                if (selected == "搜索服务器")
                {
                    var searchResult = await SearchServersAsync();
                    if (searchResult != null) return searchResult;
                    continue;
                }
                if (selected == "上一页") { offset = Math.Max(0, offset - pageSize); continue; }
                if (selected == "下一页") { offset += pageSize; continue; }

                if (mapping.TryGetValue(selected, out var item))
                    return item;
            }
        }

        static async Task<EntityNetGameItem?> SearchServersAsync()
        {
            while (true)
            {
                AnsiConsole.MarkupLine("[bold yellow]请输入搜索关键字（留空返回）：[/]");
                string? keyword;
                try
                {
                    keyword = AnsiConsole.Prompt(new TextPrompt<string>("> ").AllowEmpty());
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(keyword)) return null;

                Entities<EntityNetGameItem> servers;
                try
                {
                    servers = await _authOtp!.Api<EntityNetGameKeyword, Entities<EntityNetGameItem>>(
                        "/item/query/search-by-keyword",
                        new EntityNetGameKeyword { Keyword = keyword! });
                }
                catch
                {
                    AnsiConsole.MarkupLine("[red]搜索失败[/]");
                    continue;
                }

                if (servers.Data.Length == 0)
                {
                    AnsiConsole.MarkupLine("[red]未找到匹配的服务器[/]");
                    if (!AnsiConsole.Confirm("重新搜索？")) return null;
                    continue;
                }

                var mapping = new Dictionary<string, EntityNetGameItem>();
                var choices = new List<string>();
                for (int i = 0; i < servers.Data.Length; i++)
                {
                    var label = Markup.Escape($"{i + 1}. {servers.Data[i].Name} (ID: {servers.Data[i].EntityId})");
                    mapping[label] = servers.Data[i];
                    choices.Add(label);
                }
                choices.Add("返回");

                string selected;
                try
                {
                    selected = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold yellow]请选择服务器或返回[/]")
                            .PageSize(20)
                            .AddChoices(choices));
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (selected == "返回") continue;
                if (mapping.TryGetValue(selected, out var item)) return item;
            }
        }

        static string? TryGetJsonProperty(JsonElement element, params string[] names)
        {
            var set = new HashSet<string>(names.Select(n => n.ToLowerInvariant()));
            string? FindRecursive(JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var prop in el.EnumerateObject())
                        {
                            var nameLower = prop.Name.ToLowerInvariant();
                            if (set.Contains(nameLower))
                                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                            var nested = FindRecursive(prop.Value);
                            if (nested != null) return nested;
                        }
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in el.EnumerateArray())
                        {
                            var nested = FindRecursive(item);
                            if (nested != null) return nested;
                        }
                        break;
                }
                return null;
            }
            return FindRecursive(element);
        }

        static string? TryFindId(JsonElement element)
        {
            string? Search(JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var prop in el.EnumerateObject())
                        {
                            var n = prop.Name.ToLowerInvariant();
                            if (n.Contains("id"))
                            {
                                if (prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    var s = prop.Value.GetString();
                                    if (!string.IsNullOrWhiteSpace(s)) return s;
                                }
                                else if (prop.Value.ValueKind == JsonValueKind.Number)
                                {
                                    return prop.Value.ToString();
                                }
                            }
                            var nested = Search(prop.Value);
                            if (nested != null) return nested;
                        }
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in el.EnumerateArray())
                        {
                            var nested = Search(item);
                            if (nested != null) return nested;
                        }
                        break;
                }
                return null;
            }
            return Search(element);
        }

        

        static async Task<EntityRentalGame?> SelectRentalServerAsync()
        {
            const int pageSize = 15;
            var offset = 0;
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                AnsiConsole.MarkupLine($"[bold cyan]当前账号：[bold]{_authOtp!.EntityId}[/] ({_channel})[/]");

                Entities<EntityRentalGame> rentals;
                try
                {
                    rentals = await FetchRentalListAsync(offset);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]获取租凭服列表失败[/] [dim]{Markup.Escape(ex.Message)}[/]");
                    if (!AnsiConsole.Confirm("重试？")) { _returnToLogin = true; return null; }
                    continue;
                }

                if (rentals.Data.Length == 0)
                {
                    AnsiConsole.MarkupLine("[red]暂无租凭服或没有更多内容[/]");
                    var back = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold yellow]请选择操作[/]")
                            .AddChoices(offset > 0 ? new[] { "上一页" } : Array.Empty<string>())
                            .AddChoices(new[] { "返回主菜单" }));
                    if (back == "上一页") { offset = Math.Max(0, offset - pageSize); continue; }
                    _returnToLogin = true; return null;
                }

                var mapping = new Dictionary<string, EntityRentalGame>();
                var choices = new List<string>();
                for (int i = 0; i < rentals.Data.Length; i++)
                {
                    var globalIndex = offset + i + 1;
                    var name = string.IsNullOrWhiteSpace(rentals.Data[i].ServerName) ? rentals.Data[i].Name : rentals.Data[i].ServerName;
                    var id = rentals.Data[i].EntityId;
                    var label = Markup.Escape($"{globalIndex}. {name} (ID: {id})");
                    mapping[label] = rentals.Data[i];
                    choices.Add(label);
                }
                if (offset > 0) choices.Add("上一页");
                choices.Add("下一页");
                choices.Add("返回主菜单");

                string selected;
                try
                {
                    selected = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold yellow]请选择租凭服或操作[/]")
                            .PageSize(20)
                            .AddChoices(choices));
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (selected == "返回主菜单") { _returnToLogin = true; return null; }
                if (selected == "上一页") { offset = Math.Max(0, offset - pageSize); continue; }
                if (selected == "下一页") { offset += pageSize; continue; }

                if (mapping.TryGetValue(selected, out var item)) return item;
            }
        }

        static async Task ManageRentalServerAsync(EntityRentalGame rental)
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                AnsiConsole.MarkupLine($"[bold cyan]租凭服：[bold]{Markup.Escape(rental.Name)}[/] (ID: {rental.EntityId})[/]");

                var rentalRoles = await GetRentalServerRolesAsync(rental);
                DisplayRoles(rentalRoles);

                var operation = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold yellow]请选择操作[/]")
                        .AddChoices("启动代理", "创建随机角色", "创建指定角色", "返回服务器列表", "返回主菜单"));

                if (operation == "启动代理")
                {
                    var defaultNick = StringGenerator.GenerateRandomString(12, false);
                    var nickname = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]请输入昵称[/]:").DefaultValue(defaultNick).AllowEmpty());
                    await StartRentalProxyAsync(rental, string.IsNullOrWhiteSpace(nickname) ? "Player" : nickname);
                    return;
                }
                if (operation == "创建随机角色")
                {
                    await CreateRentalRandomCharacterAsync(rental);
                    continue;
                }
                if (operation == "创建指定角色")
                {
                    await CreateRentalNamedCharacterAsync(rental);
                    continue;
                }
                if (operation == "返回服务器列表") return;
                if (operation == "返回主菜单") { _returnToLogin = true; return; }
            }
        }

        static async Task StartRentalProxyAsync(EntityRentalGame rental, string nickname)
        {
            AnsiConsole.MarkupLine("[bold yellow]正在启动本地代理...[/]");
            AnsiConsole.MarkupLine($"[bold aqua]Nickname: {Markup.Escape(nickname)}[/]");

            try
            {
                if (string.IsNullOrWhiteSpace(rental.EntityId))
                {
                    AnsiConsole.MarkupLine("[red]启动失败：租凭服数据缺少 ID[/]");
                    Utilities.WaitForContinue();
                    return;
                }
                // 优先使用租凭服接口，避免网络服在租凭ID上返回 404
                var rentalDetails = await SafeFetchRentalDetailsAsync(rental.EntityId);
                var rentalAddr1 = await SafeFetchRentalAddressAsync(rental.EntityId, string.Empty);
                var rentalAddr2 = await FetchGatewayG79AddressAsync(rental.EntityId, string.Empty);

                string versionName;
                if (!string.IsNullOrWhiteSpace(rentalDetails?.Data?.McVersion))
                    versionName = rentalDetails!.Data!.McVersion;
                else if (!string.IsNullOrWhiteSpace(rental.McVersion))
                    versionName = rental.McVersion;
                else
                    versionName = _services!.X19.GameVersion;

                var gameVersion = GameVersionUtil.GetEnumFromGameVersion(versionName);

                EntityNetGameServerAddress? resolvedAddr = null;
                if (!string.IsNullOrWhiteSpace(rentalDetails?.Data?.ServerIp) && rentalDetails?.Data?.ServerPort is not null)
                    resolvedAddr = new EntityNetGameServerAddress { Ip = rentalDetails!.Data!.ServerIp!, Port = rentalDetails!.Data!.ServerPort!.Value };
                else if (rentalAddr1?.Data != null)
                    resolvedAddr = new EntityNetGameServerAddress { Ip = rentalAddr1!.Data!.McServerHost, Port = rentalAddr1.Data.McServerPort };
                else if (rentalAddr2?.Data != null)
                    resolvedAddr = rentalAddr2.Data;

                // 若租凭服接口不可用，再尝试网络服接口
                if (resolvedAddr == null)
                {
                    try
                    {
                        var netDetails = await _authOtp!.Api<EntityQueryNetGameDetailRequest, Entity<EntityQueryNetGameDetailItem>>(
                            "/item-details/get_v2",
                            new EntityQueryNetGameDetailRequest { ItemId = rental.EntityId });
                        var netAddress = await _authOtp.Api<EntityAddressRequest, Entity<EntityNetGameServerAddress>>(
                            "/item-address/get",
                            new EntityAddressRequest { ItemId = rental.EntityId });
                        if (netDetails.Data != null && netAddress.Data != null)
                        {
                            versionName = netDetails.Data.McVersionList != null && netDetails.Data.McVersionList.Length > 0
                                ? netDetails.Data.McVersionList[0].Name
                                : versionName;
                            gameVersion = GameVersionUtil.GetEnumFromGameVersion(versionName);
                            resolvedAddr = netAddress.Data;
                        }
                        else
                        {
                            // 尝试 Web 网关的 G79 列表/地址作为最终兜底
                            var gwAddr = await FetchGatewayG79AddressAsync(rental.EntityId, string.Empty);
                            if (gwAddr?.Data != null)
                                resolvedAddr = gwAddr.Data;
                        }
                    }
                    catch { }
                }

                if (resolvedAddr == null)
                {
                    var pwd = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]服务器可能需要密码，输入密码（可留空）[/]:").AllowEmpty());
                    if (!string.IsNullOrWhiteSpace(pwd))
                    {
                        var r1 = await SafeFetchRentalAddressAsync(rental.EntityId, pwd);
                        var r2 = await FetchGatewayG79AddressAsync(rental.EntityId, pwd);
                        if (r1?.Data != null)
                            resolvedAddr = new EntityNetGameServerAddress { Ip = r1.Data.McServerHost, Port = r1.Data.McServerPort };
                        else if (r2?.Data != null)
                            resolvedAddr = r2.Data;
                    }
                }

                if (resolvedAddr == null)
                {
                    AnsiConsole.MarkupLine("[red]无法获取服务器详情[/]");
                    Utilities.WaitForContinue();
                    return;
                }

                

                var versionName2 = versionName;
                var gameVersion2 = gameVersion;

                var serverModInfo = await InstallerService.InstallGameMods(
                    _authOtp.EntityId,
                    _authOtp.Token,
                    gameVersion2,
                    new WPFLauncher(),
                    rental.EntityId,
                    false);

                var mods = JsonSerializer.Serialize(serverModInfo);

                CreateRentalProxyInterceptor(rental, versionName2, resolvedAddr, mods, nickname);

                await X19.InterconnectionApi.GameStartAsync(_authOtp.EntityId, _authOtp.Token, rental.EntityId);
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

        static async Task<EntityGameCharacter[]> GetRentalServerRolesAsync(EntityRentalGame rental)
        {
            var result = await _authOtp!.Api<EntityQueryGameCharacters, Entities<EntityGameCharacter>>(
                "/game-character/query/user-game-characters",
                new EntityQueryGameCharacters
                {
                    GameId = rental.EntityId,
                    UserId = _authOtp.EntityId
                });
            return result.Data;
        }

        static async Task CreateRentalRandomCharacterAsync(EntityRentalGame rental)
        {
            var name = StringGenerator.GenerateRandomString(12, false);
            await CreateRentalCharacterAsync(rental, name);
            AnsiConsole.MarkupLine($"[green]已创建随机角色：[bold]{name}[/][/]");
            Utilities.WaitForContinue();
        }

        static async Task CreateRentalNamedCharacterAsync(EntityRentalGame rental)
        {
            var name = AnsiConsole.Prompt(new TextPrompt<string>("[yellow]请输入角色名称:[/]").AllowEmpty());
            if (string.IsNullOrWhiteSpace(name))
            {
                AnsiConsole.MarkupLine("[red]角色名称不能为空[/]");
                Utilities.WaitForContinue();
                return;
            }
            await CreateRentalCharacterAsync(rental, name);
            AnsiConsole.MarkupLine($"[green]已创建角色：[bold]{name}[/]");
            Utilities.WaitForContinue();
        }

        static async Task CreateRentalCharacterAsync(EntityRentalGame rental, string name)
        {
            try
            {
                await _authOtp!.Api<EntityCreateCharacter, JsonElement>(
                    "/game-character",
                    new EntityCreateCharacter
                    {
                        GameId = rental.EntityId,
                        UserId = _authOtp.EntityId,
                        Name = name
                    });
            }
            catch { }
        }

        static async Task<Entities<EntityRentalGame>> FetchRentalListAsync(int offset)
        {
            var raw = await _authOtp!.Api<EntityQueryRentalGame, JsonElement>(
                "/rental-server/query/available-public-server",
                new EntityQueryRentalGame { Offset = offset, SortType = 0 });

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return JsonSerializer.Deserialize<Entities<EntityRentalGame>>(raw.GetRawText(), options)!;
        }

        static async Task<Entity<EntityRentalGameDetails>> FetchRentalDetailsAsync(string serverId)
        {
            var raw = await _authOtp!.Api<EntityQueryRentalGameDetail, JsonElement>(
                "/rental-server/query/server-detail",
                new EntityQueryRentalGameDetail { ServerId = serverId });
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return JsonSerializer.Deserialize<Entity<EntityRentalGameDetails>>(raw.GetRawText(), options)!;
        }

        static async Task<Entity<EntityRentalGameServerAddress>> FetchRentalAddressAsync(string serverId, string pwd)
        {
            var raw = await _authOtp!.Api<EntityQueryRentalGameServerAddress, JsonElement>(
                "/rental-server/query/server-address",
                new EntityQueryRentalGameServerAddress { ServerId = serverId, Password = pwd });
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return JsonSerializer.Deserialize<Entity<EntityRentalGameServerAddress>>(raw.GetRawText(), options)!;
        }

        static async Task<Entity<EntityRentalGameDetails>?> SafeFetchRentalDetailsAsync(string serverId)
        {
            try { return await FetchRentalDetailsAsync(serverId); } catch { return null; }
        }

        static async Task<Entity<EntityRentalGameServerAddress>?> SafeFetchRentalAddressAsync(string serverId, string pwd)
        {
            try { return await FetchRentalAddressAsync(serverId, pwd); } catch { return null; }
        }

        static async Task<Entity<EntityNetGameServerAddress>?> FetchGatewayG79AddressAsync(string gameId, string pwd)
        {
            try
            {
                var http = new HttpWrapper("https://service.codexus.today",
                    o => o.WithBearerToken("0e9327a2-d0f8-41d5-8e23-233de1824b9a.pk_053ff2d53503434bb42fe158"));
                var payload = new { game = gameId, password = pwd };
                var resp = await http.PostJsonAsync("/gateway/rental-game/g79/address", payload);
                var json = await resp.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<Entity<EntityNetGameServerAddress>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return null; }
        }

        static void CreateRentalProxyInterceptor(
            EntityRentalGame rental,
            string versionName,
            EntityNetGameServerAddress address,
            string mods,
            string nickname)
        {
            Interceptor.CreateInterceptor(
                new EntitySocks5 { Enabled = false },
                mods,
                rental.EntityId,
                rental.Name,
                versionName,
                address.Ip,
                address.Port,
                nickname,
                _authOtp!.EntityId,
                _authOtp.Token,
                YggdrasilCallback);

            void YggdrasilCallback(string serverId)
            {
                var pair = Md5Mapping.GetMd5FromGameVersion(versionName);
                var signal = new SemaphoreSlim(0);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var success = await _services!.Yggdrasil.JoinServerAsync(new GameProfile
                        {
                            GameId = rental.EntityId,
                            GameVersion = versionName,
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
                if (ActiveProxies.Any(p => string.Equals(p.Nickname, character.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    AnsiConsole.MarkupLine("[red]该角色名已有正在运行的代理[/]");
                    Utilities.WaitForContinue();
                    return;
                }
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

                var lt = StartLogTerminal(server.EntityId);
                UseProxyLogger(server.EntityId, lt.writer);

                var interceptor = CreateProxyInterceptor(server, character, version, address.Data!, mods);
                var localPort = GetLocalPortFromInterceptor(interceptor, address.Data!.Port);
                AnsiConsole.MarkupLine("[green]代理配置完成[/]");
                AnsiConsole.MarkupLine($"版本: {Markup.Escape(version.Name)} | 地址: {Markup.Escape(address.Data!.Ip)}:{address.Data!.Port} | 本地端口: {localPort}");
                AnsiConsole.MarkupLine($"角色: {Markup.Escape(character.Name)}");

                await X19.InterconnectionApi.GameStartAsync(_authOtp.EntityId, _authOtp.Token, server.EntityId);
                AnsiConsole.MarkupLine("[green]代理已启动[/] [dim](按任意键返回服务器列表)[/]");
                RegisterActiveProxy(server.EntityId, server.Name, character.Name, localPort, lt.writer, lt.pid, interceptor);
                Console.ReadKey(true);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]启动失败：[bold]{ex.Message}[/][/]");
                Utilities.WaitForContinue();
            }
        }

        static object CreateProxyInterceptor(
            EntityNetGameItem server,
            EntityGameCharacter character,
            EntityMcVersion version,
            EntityNetGameServerAddress address,
            string mods)
        {
            return Interceptor.CreateInterceptor(
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
            _originalLogger = Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        static void UseProxyLogger(string serverId, StreamWriter fileWriter)
        {
            try
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.Sink(new ProxyFileSink(fileWriter))
                    .CreateLogger();
            }
            catch { }
        }

        static void RestoreLogger()
        {
            if (_originalLogger != null) Log.Logger = _originalLogger;
        }

        static int GetLocalPortFromInterceptor(object interceptor, int defaultPort)
        {
            try
            {
                var t = interceptor.GetType();
                string[] names = { "LocalPort", "RtcpPort", "ProxyPort", "ListeningPort", "LocalTcpPort", "Port" };
                foreach (var n in names)
                {
                    var p = t.GetProperty(n);
                    if (p != null && p.PropertyType == typeof(int))
                    {
                        var v = (int)p.GetValue(interceptor)!;
                        if (v > 0) return v;
                    }
                    var m = t.GetMethod(n, Type.EmptyTypes);
                    if (m != null && m.ReturnType == typeof(int))
                    {
                        var v = (int)m.Invoke(interceptor, Array.Empty<object>())!;
                        if (v > 0) return v;
                    }
                }
                foreach (var p in t.GetProperties())
                {
                    if (p.PropertyType == typeof(int))
                    {
                        var v = (int)(p.GetValue(interceptor) ?? 0);
                        if (v > 0) return v;
                    }
                }
            }
            catch { }
            return defaultPort;
        }

        static async Task InitializeSystemComponentsAsync()
        {
            Interceptor.EnsureLoaded();
            PacketManager.Instance.EnsureRegistered();
            PluginManager.Instance.EnsureUninstall();
            PluginManager.Instance.LoadPlugins("plugins");
            await Task.CompletedTask;
        }

        static void RunLogViewer(string serverId)
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var logDir = Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(logDir);
                var path = Path.Combine(logDir, string.IsNullOrWhiteSpace(serverId) ? "proxy.log" : $"{serverId}.log");
                long pos = 0;
                try
                {
                    using var initFs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
                    pos = initFs.Length;
                }
                catch { }
                DisplayHeader();
                AnsiConsole.MarkupLine("[grey]实时代理日志[/]\n");
                while (true)
                {
                    using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (pos > fs.Length) pos = fs.Length;
                        if (fs.Length > pos)
                        {
                            fs.Seek(pos, SeekOrigin.Begin);
                            var toRead = (int)(fs.Length - pos);
                            var buffer = new byte[toRead];
                            var read = fs.Read(buffer, 0, toRead);
                            if (read > 0)
                            {
                                var text = Encoding.UTF8.GetString(buffer, 0, read);
                                foreach (var line in text.Replace("\r", string.Empty).Split('\n'))
                                {
                                    if (string.IsNullOrWhiteSpace(line)) continue;
                                    PrintColoredLog(line);
                                }
                                pos += read;
                            }
                        }
                    }
                    Thread.Sleep(200);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"日志查看器错误: {ex.Message}");
                Console.ReadKey(true);
            }
        }

        static void PrintColoredLog(string line)
        {
            var escaped = Markup.Escape(line);
            string color = "white";
            if (line.Contains("[Error]", StringComparison.OrdinalIgnoreCase)) color = "red";
            else if (line.Contains("[Warning]", StringComparison.OrdinalIgnoreCase)) color = "yellow";
            else if (line.Contains("[Debug]", StringComparison.OrdinalIgnoreCase)) color = "grey";

            var l = escaped;
            l = l.Replace("Protocol version:", "[bold]Protocol version:[/]");
            l = RegexReplace(l, @"Next state:\s*([A-Za-z]+)", m => $"Next state: [lime]{m.Groups[1].Value}[/]");
            l = RegexReplace(l, @"Address:\s*([^\s]+)", m => $"Address: [aqua]{m.Groups[1].Value}[/]");
            l = RegexReplace(l, @"has joined the server\.", m => "[lime]has joined the server.[/]");

            AnsiConsole.MarkupLine($"[{color}]{l}[/]");
        }

        static string RegexReplace(string input, string pattern, Func<System.Text.RegularExpressions.Match, string> repl)
        {
            try
            {
                return System.Text.RegularExpressions.Regex.Replace(input, pattern, m => repl(m));
            }
            catch { return input; }
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

        static MultiTextWriter? _logMux;
        static TextWriter? _originalOut;
        static void EnsureLogMux()
        {
            if (_logMux != null) return;
            try
            {
                _originalOut = Console.Out;
                _logMux = new MultiTextWriter(_originalOut);
                Console.SetOut(_logMux);
            }
            catch { }
        }

        static bool _viewerStarted;
        static (StreamWriter writer, int pid) StartLogTerminal(string serverId)
        {
            var baseDir = AppContext.BaseDirectory;
            var logDir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "proxy.log");
            var writer = new StreamWriter(new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            int pid = -1;
            if (!_viewerStarted)
            {
                try
                {
                    var exe = Path.Combine(baseDir, "OpenSDK.NEL.Samse.exe");
                    var psi = new ProcessStartInfo { FileName = exe, Arguments = "--log-viewer", UseShellExecute = true };
                    var p = Process.Start(psi);
                    pid = p?.Id ?? -1;
                    _viewerStarted = true;
                }
                catch { }
            }
            return (writer, pid);
        }

        class MultiTextWriter : TextWriter
        {
            readonly List<TextWriter> writers = new List<TextWriter>();
            public MultiTextWriter(TextWriter baseWriter) { writers.Add(baseWriter); }
            public void Add(TextWriter w) { lock (writers) writers.Add(w); }
            public void Remove(TextWriter w) { lock (writers) writers.Remove(w); }
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) { lock (writers) foreach (var w in writers) w.Write(value); }
            public override void Write(char[] buffer, int index, int count) { lock (writers) foreach (var w in writers) w.Write(buffer, index, count); }
            public override void Write(string? value) { lock (writers) foreach (var w in writers) w.Write(value); }
            public override void Flush() { lock (writers) foreach (var w in writers) w.Flush(); }
        }

        static readonly List<RunningProxy> ActiveProxies = new List<RunningProxy>();

        static void RegisterActiveProxy(string serverId, string serverName, string nickname, int port, StreamWriter? writer, int viewerPid, object? interceptor)
        {
            ActiveProxies.Add(new RunningProxy { LaunchId = Guid.NewGuid(), ServerId = serverId, ServerName = serverName, Nickname = nickname, Port = port, StartedAt = DateTime.Now, LogWriter = writer, ViewerPid = viewerPid, InterceptorHandle = interceptor });
        }

        static async Task ManageProxiesAsync()
        {
            while (true)
            {
                AnsiConsole.Clear();
                DisplayHeader();
                if (ActiveProxies.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]暂无运行中的代理[/]");
                    Utilities.WaitForContinue();
                    return;
                }

                var mapping = new Dictionary<string, RunningProxy>();
                var choices = new List<string>();
                for (int i = 0; i < ActiveProxies.Count; i++)
                {
                    var p = ActiveProxies[i];
                    var label = Markup.Escape($"{i + 1}. {p.ServerName} (Port: {p.Port}) - {p.Nickname} [{p.LaunchId}]");
                    mapping[label] = p;
                    choices.Add(label);
                }
                choices.Add("关闭所有代理");
                choices.Add("返回主菜单");

                string selected;
                try
                {
                    selected = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("[bold yellow]选择要管理的代理[/]").PageSize(20).AddChoices(choices));
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (selected == "返回主菜单") return;
                if (selected == "关闭所有代理")
                {
                    foreach (var p in ActiveProxies.ToArray()) TryCloseProxyByPort(p.Port);
                    ActiveProxies.Clear();
                    AnsiConsole.MarkupLine("[green]已请求关闭所有代理[/]");
                    Utilities.WaitForContinue();
                    return;
                }

                if (mapping.TryGetValue(selected, out var proxy))
                {
                    var ok = TryCloseProxyByPort(proxy.Port);
                    if (ok)
                    {
                        ActiveProxies.RemoveAll(x => x.LaunchId == proxy.LaunchId);
                        AnsiConsole.MarkupLine("[green]代理关闭请求已发送[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]无法关闭该代理（尚未集成底层控制）[/]");
                    }
                    Utilities.WaitForContinue();
                    return;
                }
            }
        }

        static bool TryCloseProxyByPort(int port)
        {
            try
            {
                var p = ActiveProxies.Find(x => x.Port == port);
                if (p?.InterceptorHandle != null)
                {
                    var h = p.InterceptorHandle;
                    var t = h.GetType();
                    var shutdownAsync = t.GetMethod("ShutdownAsync") ?? t.GetMethod("Shutdown");
                    if (shutdownAsync != null)
                    {
                        var result = shutdownAsync.Invoke(h, Array.Empty<object>());
                        if (result is Task task) task.GetAwaiter().GetResult();
                        if (p.LogWriter != null) { try { p.LogWriter.Dispose(); } catch { } }
                        return true;
                    }
                }
                var gmType = Type.GetType("OpenSDK.NEL.Manager.GameManager, OpenSDK.NEL");
                if (gmType != null)
                {
                    dynamic inst = gmType.GetProperty("Instance")?.GetValue(null)!;
                    var close = gmType.GetMethod("Close");
                    if (inst != null && close != null)
                    {
                        var res = close.Invoke(inst, new object[] { p?.ServerId ?? string.Empty });
                        if (res is bool b && b) return true;
                    }
                    foreach (var name in new[] { "ShutdownInterceptor", "ShutdownLauncher", "ShutdownPeInterceptor", "ShutdownPeLauncher" })
                    {
                        var m = gmType.GetMethod(name, new[] { typeof(Guid) });
                        if (m != null && Guid.TryParse(p?.ServerId, out var gid))
                        {
                            m.Invoke(inst, new object[] { gid });
                        }
                    }
                }

                

                var interceptType = Type.GetType("Codexus.Interceptors.Interceptor, Codexus.Interceptors");
                if (interceptType != null)
                {
                    var shutdown = interceptType.GetMethod("Shutdown", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (shutdown != null)
                    {
                        shutdown.Invoke(null, Array.Empty<object>());
                        var px = ActiveProxies.Find(x => x.Port == port);
                        if (px?.LogWriter != null) { try { px.LogWriter.Dispose(); } catch { } }
                        return true;
                    }
                }

                // 扫描可能存在的其它拦截器类型
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("OpenSDK.NEL.Interceptor") ?? asm.GetType("Codexus.OpenSDK.NEL.Interceptor");
                    var shut = t?.GetMethod("Shutdown", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (shut != null)
                    {
                        shut.Invoke(null, Array.Empty<object>());
                        var px2 = ActiveProxies.Find(x => x.Port == port);
                        if (px2?.LogWriter != null) { try { px2.LogWriter.Dispose(); } catch { } }
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        internal class RunningProxy
        {
            public Guid LaunchId { get; set; }
            public string ServerId { get; set; } = "";
            public string ServerName { get; set; } = "";
            public string Nickname { get; set; } = "";
            public int Port { get; set; }
            public DateTime StartedAt { get; set; }
            public StreamWriter? LogWriter { get; set; }
            public int ViewerPid { get; set; }
            public object? InterceptorHandle { get; set; }
        }

        class ProxyFileSink : Serilog.Core.ILogEventSink
        {
            readonly StreamWriter writer;
            public ProxyFileSink(StreamWriter writer) { this.writer = writer; }
            public void Emit(Serilog.Events.LogEvent logEvent)
            {
                try
                {
                    var line = $"[{logEvent.Timestamp:HH:mm:ss} {logEvent.Level}] {logEvent.RenderMessage()}";
                    writer.WriteLine(line);
                    writer.Flush();
                }
                catch { }
            }
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