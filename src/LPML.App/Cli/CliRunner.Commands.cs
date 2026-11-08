using System.Globalization;
using System.IO;
using LPML.App.Services;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Config;
using LPML.Core.Download;
using LPML.Core.Game.Models;
using LPML.Core.Instances;
using LPML.Core.Integration;
using LPML.Core.Modrinth;
using LPML.Core.Mods;

namespace LPML.App.Cli;

/// <summary>
/// 命令行命令表。与 GUI 共用同一套 Core 服务，不带任何界面依赖。
/// 命令表放在 CliRunner.Help.cs（中英两份 C# 常量，避开 XAML 空白折叠），只通过 help 一次性输出；底部常驻提示只保留 help。
/// </summary>
public sealed partial class CliRunner
{
    private static readonly string[] ConfigKeys =
    {
        "language", "theme", "perf", "mirror", "smallconcurrency", "largeconcurrency",
        "retries", "timeout", "speedlimit", "verifysha1", "loglevel", "title",
        "guard", "guardtimeout", "autodownloadjava", "autodownloadmissing", "afterlaunch"
    };

    // ── 参数小工具 ────────────────────────────────────────

    private static string[] Tokens(string tail) => tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Arg(string[] tokens, int index) => index < tokens.Length ? tokens[index] : "";

    /// <summary>取第 skip 个词之后的全部内容（路径里可能带空格）。</summary>
    private static string Rest(string tail, int skip)
    {
        var parts = tail.Split(' ', skip + 1, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > skip ? parts[skip].Trim() : "";
    }

    private void Usage(string syntax) => _output(Lang.F("String.Cli.CmdNeedArgs", syntax));
    private void NotFound(string key) => _output(Lang.F("String.Cli.CmdNotFound", key));
    private void Failed(string message) => _output(Lang.F("String.Cli.CmdFailed", message));
    private void Ok() => _output(Lang.T("String.Cli.CmdOk"));

    private GameInstance? Pick(string key) => ResolveInstance(key, null);

    // ── 统一命令分派（游戏启动前后都可用） ────────────────

    /// <summary>返回 true 表示已处理；exit 表示同时结束输入循环。</summary>
    private bool HandleCommonCommand(string head, string tail, out bool exit)
    {
        exit = false;
        switch (head)
        {
            case "help" or "?":
                PrintFullHelp(); return true;

            case "ls" or "list" or "instances":
                ListInstances(); return true;

            case "versions":
                ListVersions(); return true;

            case "accounts":
                ListAccounts(); return true;

            case "javas":
                ListJavas(); return true;

            case "java":
                // 游戏态的裸 java 仍是「显示本次使用的 Java」，交给原来的 switch
                if (string.IsNullOrWhiteSpace(tail))
                {
                    if (!_shellMode) return false;
                    ListJavas();
                }
                else CmdJava(tail);
                return true;

            case "clear" or "cls":
                _clearScreen(); return true;

            case "status" or "stat":
                if (_shellMode) CmdLauncherStatus(); else PrintStatus();
                return true;

            case "quit" or "exit" or "q":
                if (!_shellMode) StopGame();
                exit = true; return true;

            case "stop":
                if (_shellMode) _output(Lang.T("String.Cli.CmdNoGame"));
                else { StopGame(); exit = true; }
                return true;

            case "kill":
                if (_shellMode) _output(Lang.T("String.Cli.CmdNoGame"));
                else { _process?.Kill(); _output(Lang.T("String.Cli.CmdKilled")); exit = true; }
                return true;

            case "launch" or "start":
                // CLI 一次只管理一个游戏进程；多开请用图形界面
                if (_shellMode) LaunchFromShell(tail);
                else _output(Lang.T("String.Cli.CmdNotAvailable"));
                return true;

            case "log":
                if (_shellMode) CmdLogTail(); else _output(Lang.T("String.Cli.CmdLogHint"));
                return true;

            case "instance": CmdInstance(tail); return true;
            case "mods": CmdModsList(tail); return true;
            case "mod": CmdMod(tail); return true;
            case "account": CmdAccount(tail); return true;
            case "version": CmdVersion(tail); return true;
            case "modpack": CmdModpack(tail); return true;
            case "download": CmdDownload(tail); return true;
            case "mirror": CmdMirror(tail); return true;
            case "config": CmdConfig(tail); return true;
            case "language": CmdSimpleSetting("language", tail); return true;
            case "theme": CmdSimpleSetting("theme", tail); return true;
            case "perf": CmdSimpleSetting("perf", tail); return true;
            case "paths": CmdPaths(); return true;
            case "doc" or "docs": CmdDoc(tail); return true;
            case "agreement": CmdAgreementReplay(); return true;
            case "disclaimer": CmdDisclaimer(); return true;
            case "about": CmdAbout(); return true;
            case "games": CmdGames(); return true;
            case "mp": CmdMultiplayer(); return true;

            default: return false;
        }
    }

    /// <summary>help 一次性输出完整命令表：不分页、不截断、不附加任何引导语。</summary>
    private void PrintFullHelp()
    {
        var lines = HelpTable(_ctx.Settings.Language);

        // REPL 下走全量普通输出（可回滚查看），其余模式逐行写控制台。
        if (_transcript is not null) _transcript(lines);
        else foreach (var line in lines) _output(line);
    }

    // ── 实例 ─────────────────────────────────────────────

    private void CmdInstance(string tail)
    {
        var t = Tokens(tail);
        switch (Arg(t, 0).ToLowerInvariant())
        {
            case "info":
            {
                var inst = Pick(Arg(t, 1));
                if (inst is null) { NotFound(Arg(t, 1)); return; }
                _output(Lang.F("String.Cli.CmdInstanceInfo", inst.Name, inst.VersionId,
                    inst.LoaderName ?? "vanilla", inst.Isolated ? "1" : "0", inst.PlayCount));
                _output(Lang.F("String.Cli.CmdConfigLine", "id", inst.Id));
                break;
            }
            case "create":
            {
                var name = Arg(t, 1);
                var version = Arg(t, 2);
                if (name.Length == 0 || version.Length == 0) { Usage("instance create <名称> <版本> [isolated]"); return; }
                try
                {
                    var isolated = t.Any(a => a.Equals("isolated", StringComparison.OrdinalIgnoreCase)
                                              || a.Equals("-i", StringComparison.OrdinalIgnoreCase));
                    var inst = _ctx.Instances.Create(name, version, isolated);
                    _output(Lang.F("String.Cli.CmdInstanceCreated", inst.Name, inst.VersionId));
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;
            }
            case "rename":
            {
                var inst = Pick(Arg(t, 1));
                var newName = Arg(t, 2);
                if (inst is null) { NotFound(Arg(t, 1)); return; }
                if (newName.Length == 0) { Usage("instance rename <名称> <新名>"); return; }
                if (_ctx.Instances.NameExists(newName))
                {
                    Failed(Lang.F("String.Core.Err.Instance.DuplicateName", newName));
                    return;
                }
                inst.Name = newName;
                _ctx.Instances.Save();
                _output(Lang.F("String.Cli.CmdInstanceRenamed", newName));
                break;
            }
            case "delete":
            {
                var inst = Pick(Arg(t, 1));
                if (inst is null) { NotFound(Arg(t, 1)); return; }
                if (!t.Any(a => a.Equals("-y", StringComparison.OrdinalIgnoreCase)
                                || a.Equals("--yes", StringComparison.OrdinalIgnoreCase)))
                {
                    _output(Lang.F("String.Cli.CmdNeedConfirm", $"instance delete {inst.Name} -y"));
                    return;
                }
                _ctx.Instances.Delete(inst, false);
                _output(Lang.F("String.Cli.CmdInstanceDeleted", inst.Name));
                break;
            }
            case "clone":
            {
                var inst = Pick(Arg(t, 1));
                if (inst is null) { NotFound(Arg(t, 1)); return; }
                var copy = _ctx.Instances.Clone(inst);
                var newName = Arg(t, 2);
                if (newName.Length > 0 && !_ctx.Instances.NameExists(newName))
                {
                    copy.Name = newName;
                    _ctx.Instances.Save();
                }
                _output(Lang.F("String.Cli.CmdInstanceCreated", copy.Name, copy.VersionId));
                break;
            }
            case "select":
            {
                var inst = Pick(Arg(t, 1));
                if (inst is null) { NotFound(Arg(t, 1)); return; }
                _ctx.Settings.SelectedInstanceId = inst.Id;
                _ctx.Save();
                _output(Lang.F("String.Cli.CmdInstanceSelected", inst.Name));
                break;
            }
            case "dir":
            {
                var inst = Pick(Arg(t, 1));
                if (inst is null) { NotFound(Arg(t, 1)); return; }
                _output(_ctx.Instances.InstanceDirectory(inst));
                break;
            }
            default:
                Usage("instance info|create|rename|delete|clone|select|dir <实例>");
                break;
        }
    }

    // ── 模组 ─────────────────────────────────────────────

    private void CmdModsList(string tail)
    {
        var inst = Pick(Arg(Tokens(tail), 0));
        if (inst is null) { NotFound(Arg(Tokens(tail), 0)); return; }

        var mods = ModManager.Scan(_ctx.ModsDirectoryFor(inst));
        _output(Lang.F("String.Cli.CmdModCount", mods.Count, mods.Count(m => m.Enabled)));
        foreach (var mod in mods)
            _output($"  {(mod.Enabled ? "+" : "-")} {mod.FileName,-40} {mod.SizeText,-10} {mod.Loader}");
    }

    private void CmdMod(string tail)
    {
        var t = Tokens(tail);
        switch (Arg(t, 0).ToLowerInvariant())
        {
            case "search":
            {
                var query = Rest(tail, 1);
                if (query.Length == 0) { Usage("mod search <关键词>"); return; }
                _output(Lang.F("String.Cli.CmdSearching", query));
                try
                {
                    var res = _ctx.Modrinth.SearchAsync(query, ModrinthProjectType.Mod,
                        null, null, 0, 10, "downloads", _shellToken).GetAwaiter().GetResult();
                    var hits = res.Hits ?? new List<ModrinthProject>();
                    if (hits.Count == 0) { _output(Lang.T("String.Cli.CmdModSearchEmpty")); return; }
                    _output(Lang.F("String.Cli.CmdSearchTotal", res.TotalHits));
                    foreach (var hit in hits)
                        _output(Lang.F("String.Cli.CmdModSearchResult",
                            hit.Slug ?? "-", hit.Title ?? "-", hit.Downloads));
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;
            }
            case "install":
            {
                var inst = Pick(Arg(t, 1));
                var slug = Arg(t, 2);
                if (inst is null || slug.Length == 0) { Usage("mod install <实例> <slug> [MC版本]"); return; }
                try
                {
                    var gameVersion = Arg(t, 3);
                    var versions = _ctx.Modrinth.GetVersionsAsync(slug,
                        gameVersion.Length > 0 ? new[] { gameVersion } : null,
                        null, _shellToken).GetAwaiter().GetResult();
                    if (versions.Count == 0) { NotFound(slug); return; }

                    var project = _ctx.Modrinth.GetProjectAsync(slug, _shellToken).GetAwaiter().GetResult();
                    var title = project?.Title ?? slug;
                    var item = _ctx.Modrinth.ToDownloadItem(versions[0], ModrinthProjectType.Mod,
                        _ctx.WorkingDirectoryFor(inst), title);
                    item.Section = DownloadSections.General;

                    var task = _ctx.Downloads.BeginTask(title, "mod");
                    _ctx.Downloads.MarkTaskReady();
                    _ctx.Downloads.RunWithAsync(new[] { item }, task, _shellToken).GetAwaiter().GetResult();
                    _ctx.Downloads.MarkFinished(task);

                    if (item.Status == DownloadStatus.Failed) Failed(item.Error ?? title);
                    else _output(Lang.F("String.Cli.CmdModInstalled", title));
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;
            }
            case "enable" or "disable" or "remove":
            {
                var sub = Arg(t, 0).ToLowerInvariant();
                var inst = Pick(Arg(t, 1));
                var keyword = Arg(t, 2);
                if (inst is null || keyword.Length == 0)
                {
                    Usage("mod enable|disable|remove <实例> <文件名关键字>");
                    return;
                }
                var mods = ModManager.Scan(_ctx.ModsDirectoryFor(inst));
                var mod = mods.FirstOrDefault(m =>
                    m.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (mod is null) { NotFound(keyword); return; }

                if (sub == "remove")
                {
                    _output(ModManager.Delete(mod)
                        ? Lang.F("String.Cli.CmdModRemoved", mod.FileName)
                        : Lang.F("String.Cli.CmdFailed", mod.FileName));
                }
                else
                {
                    var enable = sub == "enable";
                    var word = enable ? Lang.T("String.Cli.WordEnabled") : Lang.T("String.Cli.WordDisabled");
                    _output(ModManager.SetEnabled(mod, enable)
                        ? Lang.F("String.Cli.CmdModToggled", mod.FileName, word)
                        : Lang.F("String.Cli.CmdFailed", mod.FileName));
                }
                break;
            }
            default:
                Usage("mod search|install|enable|disable|remove ...");
                break;
        }
    }

    // ── 账户 ─────────────────────────────────────────────

    private void CmdAccount(string tail)
    {
        var t = Tokens(tail);
        switch (Arg(t, 0).ToLowerInvariant())
        {
            case "add":
            {
                var name = Arg(t, 1);
                if (name.Length == 0) { Usage("account add <名称>"); return; }
                var account = _ctx.Accounts.CreateOffline(name);
                _ctx.Accounts.Add(account);
                _ctx.Accounts.Save();
                _output(Lang.F("String.Cli.CmdAccountAdded", account.UserName));
                break;
            }
            case "remove":
            {
                var key = Arg(t, 1);
                var account = _ctx.Accounts.Accounts.FirstOrDefault(a =>
                    a.UserName.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (account is null) { NotFound(key); return; }
                _ctx.Accounts.Remove(account);
                _ctx.Accounts.Save();
                _output(Lang.F("String.Cli.CmdAccountRemoved", account.UserName));
                break;
            }
            case "select":
            {
                var key = Arg(t, 1);
                var account = _ctx.Accounts.Accounts.FirstOrDefault(a =>
                    a.UserName.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (account is null) { NotFound(key); return; }
                _ctx.Settings.SelectedAccountId = account.Id;
                _ctx.Save();
                _output(Lang.F("String.Cli.CmdAccountSelected", account.UserName));
                break;
            }
            case "refresh":
                try
                {
                    _ctx.Accounts.RefreshAllAsync(_shellToken).GetAwaiter().GetResult();
                    Ok();
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;
            default:
                Usage("account add|remove|select|refresh ...");
                break;
        }
    }

    // ── 版本与整合包 ─────────────────────────────────────

    private void CmdVersion(string tail)
    {
        var t = Tokens(tail);
        switch (Arg(t, 0).ToLowerInvariant())
        {
            case "list":
                try
                {
                    var manifest = _ctx.ManifestService.GetManifestAsync(false, _shellToken)
                        .GetAwaiter().GetResult();
                    var versions = manifest?.Versions ?? new List<ManifestVersion>();
                    _output(Lang.F("String.Cli.CmdVersionList", Math.Min(20, versions.Count)));
                    foreach (var v in versions.Take(20)) _output($"  {v.Id,-16} {v.Type}");
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;

            case "install":
            {
                var id = Arg(t, 1);
                if (id.Length == 0) { Usage("version install <版本ID>"); return; }
                try
                {
                    var manifest = _ctx.ManifestService.GetManifestAsync(false, _shellToken)
                        .GetAwaiter().GetResult();
                    var entry = manifest?.Versions.FirstOrDefault(v =>
                        v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (entry is null) { NotFound(id); return; }

                    var json = _ctx.ManifestService.FetchVersionJsonAsync(entry, _shellToken)
                        .GetAwaiter().GetResult();
                    if (json is null) { Failed(id); return; }

                    var items = _ctx.Installer.BuildInstallItemsAsync(json, true, _shellToken)
                        .GetAwaiter().GetResult();
                    _output(Lang.F("String.Cli.CmdVersionInstall", id, items.Count));

                    var task = _ctx.Downloads.BeginTask(id, "game");
                    _ctx.Downloads.MarkTaskReady();
                    _ctx.Downloads.Enqueue(items, task);
                    _ctx.Downloads.RunAsync(_shellToken).GetAwaiter().GetResult();
                    _ctx.Downloads.MarkFinished(task);

                    var failed = items.Count(i => i.Status == DownloadStatus.Failed);
                    _output(Lang.F("String.Cli.CmdVersionDone", id, failed));
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;
            }
            default:
                Usage("version list|install ...");
                break;
        }
    }

    private void CmdModpack(string tail)
    {
        var path = Rest(tail, 1);
        if (path.Length == 0) { Usage("modpack info <文件路径>"); return; }
        if (!File.Exists(path)) { NotFound(path); return; }
        try
        {
            var info = ModpackProbe.Inspect(path);
            _output(Lang.F("String.Cli.CmdConfigLine", "format", info.FormatText));
            _output(Lang.F("String.Cli.CmdConfigLine", "name", info.Name));
            _output(Lang.F("String.Cli.CmdConfigLine", "version", info.GameVersion ?? "-"));
            _output(Lang.F("String.Cli.CmdConfigLine", "loader",
                $"{info.LoaderName ?? "-"} {info.LoaderVersion ?? ""}".Trim()));
            _output(info.Summary);
        }
        catch (Exception ex) { Failed(ex.Message); }
    }

    // ── 下载中心 ─────────────────────────────────────────

    private void CmdDownload(string tail)
    {
        var t = Tokens(tail);
        switch (Arg(t, 0).ToLowerInvariant())
        {
            case "status":
            {
                var agg = _ctx.Downloads.BuildAggregate();
                _output(Lang.F("String.Cli.CmdDownloadSummary",
                    agg.Completed, agg.Total, agg.Failed, agg.Active));
                break;
            }
            case "tasks":
                foreach (var task in _ctx.Downloads.Tasks)
                {
                    var items = task.SnapshotItems();
                    _output(Lang.F("String.Cli.CmdTaskLine", task.Title,
                        items.Count(i => i.Status == DownloadStatus.Completed),
                        items.Count, task.Finished ? "done" : "running"));
                }
                break;

            case "pause":
                _ctx.Downloads.Pause(); Ok(); break;

            case "resume":
                try { _ctx.Downloads.ResumeIncompleteAsync(_shellToken).GetAwaiter().GetResult(); Ok(); }
                catch (Exception ex) { Failed(ex.Message); }
                break;

            case "retry":
            {
                var bad = _ctx.Downloads.Items.Where(i => i.Status is DownloadStatus.Failed
                    or DownloadStatus.Canceled or DownloadStatus.Paused).ToList();
                if (bad.Count == 0) { Ok(); return; }
                try { _ctx.Downloads.RedownloadItemsAsync(bad, _shellToken).GetAwaiter().GetResult(); Ok(); }
                catch (Exception ex) { Failed(ex.Message); }
                break;
            }
            case "cancel":
                _ctx.Downloads.CancelAll(); Ok(); break;

            case "clear":
                _ctx.Downloads.ClearFinishedTasks(); Ok(); break;

            default:
                Usage("download status|tasks|pause|resume|retry|cancel|clear");
                break;
        }
    }

    // ── Java ─────────────────────────────────────────────

    private void CmdJava(string tail)
    {
        var t = Tokens(tail);
        switch (Arg(t, 0).ToLowerInvariant())
        {
            case "list" or "ls":
                ListJavas(); break;

            case "scan":
                try
                {
                    _ctx.Javas.SyncFromEnvironment();
                    _ctx.Javas.ScanSystem();
                    _ctx.Javas.Validate();
                    _ctx.Javas.Save();
                    _output(Lang.F("String.Cli.CmdJavaScanned", _ctx.Javas.Javas.Count));
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;

            case "add":
            {
                var path = Rest(tail, 1);
                if (path.Length == 0) { Usage("java add <java.exe 路径>"); return; }
                try
                {
                    var added = _ctx.Javas.Add(path);
                    _ctx.Javas.Save();
                    _output(Lang.F("String.Cli.CmdJavaAdded", added.Count));
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;
            }
            case "remove":
            {
                var key = Rest(tail, 1);
                var java = _ctx.Javas.Javas.FirstOrDefault(j =>
                    j.Path.Contains(key, StringComparison.OrdinalIgnoreCase));
                if (java is null) { NotFound(key); return; }
                _ctx.Javas.Remove(java);
                _ctx.Javas.Save();
                Ok();
                break;
            }
            default:
                Usage("java list|scan|add|remove ...");
                break;
        }
    }

    // ── 镜像 ─────────────────────────────────────────────

    private void CmdMirror(string tail)
    {
        var t = Tokens(tail);
        switch (Arg(t, 0).ToLowerInvariant())
        {
            case "list" or "status":
                _output(Lang.F("String.Cli.CmdMirrorList", _ctx.Settings.Mirror.Default,
                    string.Join(", ", _ctx.Mirrors.FailoverOrder(ResourceCategory.Libraries))));
                break;

            case "probe":
                try
                {
                    var results = _ctx.Mirrors.MeasureAsync(async (url, ct) =>
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(5));
                        await _ctx.Http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
                    }, _shellToken).GetAwaiter().GetResult();
                    foreach (var r in results)
                        _output(Lang.F("String.Cli.CmdMirrorProbe", r.Kind, r.ElapsedMs));
                    if (results.Count == 0) _output(Lang.T("String.Cli.CmdModSearchEmpty"));
                }
                catch (Exception ex) { Failed(ex.Message); }
                break;

            case "set":
                CmdSimpleSetting("mirror", Arg(t, 1));
                break;

            default:
                Usage("mirror list|probe|set <源>");
                break;
        }
    }

    // ── 设置 ─────────────────────────────────────────────

    private void CmdConfig(string tail)
    {
        var t = Tokens(tail);
        switch (Arg(t, 0).ToLowerInvariant())
        {
            case "show":
                foreach (var key in ConfigKeys)
                    _output(Lang.F("String.Cli.CmdConfigLine", key, GetSettingValue(key) ?? "-"));
                break;

            case "get":
            {
                var key = Arg(t, 1).ToLowerInvariant();
                var value = GetSettingValue(key);
                if (value is null) _output(Lang.F("String.Cli.CmdConfigUnknown", key));
                else _output(Lang.F("String.Cli.CmdConfigLine", key, value));
                break;
            }
            case "set":
            {
                var key = Arg(t, 1).ToLowerInvariant();
                var value = Arg(t, 2);
                if (key.Length == 0 || value.Length == 0) { Usage("config set <键> <值>"); return; }
                if (SetSettingValue(key, value)) _output(Lang.F("String.Cli.CmdConfigSet", key, value));
                else _output(Lang.F("String.Cli.CmdConfigUnknown", key));
                break;
            }
            default:
                Usage("config show|get|set ...");
                break;
        }
    }

    private void CmdSimpleSetting(string key, string value)
    {
        if (value.Length == 0) { Usage(key + " <值>"); return; }
        if (SetSettingValue(key, value)) _output(Lang.F("String.Cli.CmdConfigSet", key, value));
        else _output(Lang.F("String.Cli.CmdConfigUnknown", key));
    }

    /// <summary>只开放一批标量设置，避免把复杂对象直接暴露给命令行。</summary>
    private string? GetSettingValue(string key)
    {
        var s = _ctx.Settings;
        return key.ToLowerInvariant() switch
        {
            "language" => s.Language.ToString(),
            "theme" => s.Appearance.Theme.ToString(),
            "perf" => s.Appearance.PerformanceMode.ToString(),
            "mirror" => s.Mirror.Default.ToString(),
            "smallconcurrency" => s.Download.SmallFileConcurrency.ToString(CultureInfo.InvariantCulture),
            "largeconcurrency" => s.Download.LargeFileConcurrency.ToString(CultureInfo.InvariantCulture),
            "retries" => s.Download.RetryCount.ToString(CultureInfo.InvariantCulture),
            "timeout" => s.Download.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "speedlimit" => s.Download.SpeedLimitKb.ToString(CultureInfo.InvariantCulture),
            "verifysha1" => s.Download.VerifySha1.ToString(),
            "loglevel" => s.Advanced.LogLevel.ToString(),
            "title" => s.Appearance.LauncherTitle,
            "guard" => s.Advanced.EnableGuardProcess.ToString(),
            "guardtimeout" => s.Advanced.GuardTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "autodownloadjava" => s.Java.AutoDownloadJavaRuntime.ToString(),
            "autodownloadmissing" => s.Launch.AutoDownloadMissingFiles.ToString(),
            "afterlaunch" => s.Launch.AfterLaunch.ToString(),
            _ => null
        };
    }

    private bool SetSettingValue(string key, string value)
    {
        var s = _ctx.Settings;
        var ok = true;
        switch (key.ToLowerInvariant())
        {
            case "language":
                if (value is "zh" or "zh-cn" or "zhhans" or "0") s.Language = LauncherLanguage.ZhHans;
                else if (value is "en" or "en-us" or "english" or "1") s.Language = LauncherLanguage.English;
                else ok = false;
                break;
            case "theme":
                ok = Enum.TryParse<ThemeMode>(value, true, out var theme) && Enum.IsDefined(theme);
                if (ok) s.Appearance.Theme = theme;
                break;
            case "perf":
                ok = Enum.TryParse<PerformanceMode>(value, true, out var perf) && Enum.IsDefined(perf);
                if (ok) s.Appearance.PerformanceMode = perf;
                break;
            case "mirror":
                ok = Enum.TryParse<MirrorKind>(value, true, out var mirror)
                     && Enum.IsDefined(mirror) && mirror != MirrorKind.MCBBS;
                if (ok) { s.Mirror.Default = mirror; _ctx.Mirrors.Refresh(); }
                break;
            case "smallconcurrency": ok = TryInt(value, out var small); if (ok) s.Download.SmallFileConcurrency = small; break;
            case "largeconcurrency": ok = TryInt(value, out var large); if (ok) s.Download.LargeFileConcurrency = large; break;
            case "retries": ok = TryInt(value, out var retries); if (ok) s.Download.RetryCount = retries; break;
            case "timeout": ok = TryInt(value, out var timeout); if (ok) s.Download.TimeoutSeconds = timeout; break;
            case "speedlimit": ok = TryInt(value, out var limit); if (ok) s.Download.SpeedLimitKb = limit; break;
            case "verifysha1": ok = bool.TryParse(value, out var verify); if (ok) s.Download.VerifySha1 = verify; break;
            case "loglevel":
                ok = Enum.TryParse<LogLevel>(value, true, out var level) && Enum.IsDefined(level);
                if (ok) s.Advanced.LogLevel = level;
                break;
            case "title": s.Appearance.LauncherTitle = value; break;
            case "guard": ok = bool.TryParse(value, out var guard); if (ok) s.Advanced.EnableGuardProcess = guard; break;
            case "guardtimeout": ok = TryInt(value, out var guardTimeout); if (ok) s.Advanced.GuardTimeoutSeconds = guardTimeout; break;
            case "autodownloadjava": ok = bool.TryParse(value, out var autoJava); if (ok) s.Java.AutoDownloadJavaRuntime = autoJava; break;
            case "autodownloadmissing": ok = bool.TryParse(value, out var autoMissing); if (ok) s.Launch.AutoDownloadMissingFiles = autoMissing; break;
            case "afterlaunch":
                ok = Enum.TryParse<AfterLaunchAction>(value, true, out var after) && Enum.IsDefined(after);
                if (ok) s.Launch.AfterLaunch = after;
                break;
            default: ok = false; break;
        }

        if (!ok) return false;
        try { _ctx.ApplySettings(); _ctx.Save(); }
        catch (Exception ex) { Failed(ex.Message); return false; }
        return true;
    }

    private static bool TryInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    // ── 路径 / 文档 / 关于 / 日志 ───────────────────────

    private void CmdPaths()
    {
        var text = Lang.F("String.Cli.CmdPaths", _ctx.Paths.RootDirectory, _ctx.Paths.GameDirectory,
            _ctx.Paths.ConfigDirectory, _ctx.Paths.LogDirectory, _ctx.Paths.DocsDirectory,
            _ctx.Paths.SettingsFile);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n')) _output(line);
    }

    private void CmdDoc(string tail)
    {
        var name = Arg(Tokens(tail), 0).ToLowerInvariant();
        if (name.Length == 0) name = "guide";
        if (name is "disclaimer" or "license") { CmdDisclaimer(); return; }

        var path = CliAgreement.FindMarkdownPath(_ctx, name + ".md");
        if (path is null) { _output(Lang.F("String.Cli.CmdDocMissing", name)); return; }
        try
        {
            var lines = CliAgreement.ToPlainText(File.ReadAllText(path)).Split('\n');
            foreach (var line in lines.Take(60)) _output(line);
            if (lines.Length > 60) _output(Lang.F("String.Cli.CmdDocMore", lines.Length, path));
        }
        catch (Exception ex) { Failed(ex.Message); }
    }

    private void CmdAgreementReplay()
    {
        _output(Lang.F("String.Cli.CmdAgreementReplay", AgreementWindow.CurrentVersion));
        var markdown = CliAgreement.FindMarkdown(_ctx, "agreement.md")
                       ?? L.T("String.Agreement.FallbackBody");
        foreach (var line in CliAgreement.ToPlainText(markdown).Split('\n')) _output(line);
    }

    private void CmdDisclaimer() =>
        _output(Lang.F("String.Cli.CmdDisclaimer", CliAgreement.DisclaimerPath(_ctx)));

    private void CmdAbout()
    {
        var text = Lang.F("String.Cli.CmdAbout", _ctx.Settings.Language, AgreementWindow.CurrentVersion);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n')) _output(line);
    }

    private void CmdLogTail()
    {
        var file = _ctx.Paths.LauncherLogFile;
        if (!File.Exists(file)) { _output(Lang.F("String.Cli.CmdDocMissing", file)); return; }
        _output(Lang.F("String.Cli.CmdLogTail", file));
        try
        {
            foreach (var line in File.ReadLines(file).TakeLast(20)) _output(line);
        }
        catch (Exception ex) { Failed(ex.Message); }
    }

    private void CmdLauncherStatus()
    {
        var inst = ResolveInstance(null, null);
        var account = ResolveAccount(null);
        _output(Lang.F("String.Cli.CmdStatusLauncher",
            _ctx.Instances.Instances.Count,
            _ctx.Accounts.Accounts.Count,
            _ctx.Javas.Javas.Count,
            _ctx.Downloads.Tasks.Count,
            inst?.Name ?? "-",
            account?.UserName ?? "-"));
    }

    private void CmdGames()
    {
        if (_process is null || !_process.IsRunning) { _output(Lang.T("String.Cli.CmdNoGame")); return; }
        _output(Lang.F("String.Cli.CmdGamesLine",
            _instance?.Name ?? "-", _process.ProcessId?.ToString() ?? "-"));
    }

    private void CmdMultiplayer()
    {
        var t = _ctx.Settings.Terracotta;
        _output(Lang.F("String.Cli.CmdMpStatus",
            string.IsNullOrWhiteSpace(t.InstalledVersion) ? "-" : t.InstalledVersion,
            string.IsNullOrWhiteSpace(t.LastRoomCode) ? "-" : t.LastRoomCode,
            t.LastMode.ToString()));
    }
}
