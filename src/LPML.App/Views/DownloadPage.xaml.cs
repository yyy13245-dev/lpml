using LPML.App.Services;
using LPML.Core;
using LPML.Core.Integration;
using LPML.Core.Java;
using LPML.Core.Download;
using LPML.Core.Game;
using LPML.Core.Game.Models;
using LPML.Core.Instances;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using LPML.App.Windows;

namespace LPML.App.Views;

public partial class DownloadPage : UserControl, IRefreshablePage, IParameterizedPage
{
    public LauncherContext Context { get; }

    private List<ManifestVersion> _allVersions = new();
    private string _currentType = "release";
    private string _search = "";

    /// <summary>XAML 解析完成标志：初始化期间控件未必全部创建，事件需跳过。</summary>
    private bool _ready;

    public DownloadPage(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        _ready = true;
        Loaded += async (_, _) => await InitializeAsync();
    }

    /// <summary>每次从侧边栏进入时刷新（IRefreshablePage）。</summary>
    public void Refresh()
    {
        if (!_ready) return;
        ApplyFilter();
    }

    /// <summary>由侧边栏子项决定显示哪个面板（"安装游戏" / "整合包导入"）。</summary>
    public void ApplyArguments(string arguments)
    {
        var tab = "";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            var parts = arguments.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "tab") tab = parts[1];
        }
        ShowPane(tab == "pack" ? "pack" : "install");
    }

    private void ShowPane(string pane)
    {
        if (PaneInstall is null || PaneModpack is null) return;
        var isPack = pane == "pack";
        PaneInstall.Visibility = isPack ? Visibility.Collapsed : Visibility.Visible;
        PaneModpack.Visibility = isPack ? Visibility.Visible : Visibility.Collapsed;
        if (isPack) _ = LoadOnlinePacksAsync();
    }

    private async Task InitializeAsync()
    {
        if (_allVersions.Count > 0) { ApplyFilter(); return; }

        VersionStatusText.Text = Lang.T("String.Download.LoadingVersions");
        try
        {
            var manifest = await Context.ManifestService.GetManifestAsync();
            _allVersions = manifest?.Versions ?? new List<ManifestVersion>();
        }
        catch (Exception ex)
        {
            Log.Error("Download", "Fetch manifest failed", ex);
        }

        ApplyFilter();

        // 拿不到列表要说明原因，否则用户只看到一片空白
        if (_allVersions.Count == 0)
        {
            VersionStatusText.Text = Lang.F("String.Download.NoVersions",
                Context.Settings.Mirror.Default.ToString()) + " " + Lang.T("String.Download.NoVersionsHint");
            return;
        }

        VersionStatusText.Text = Lang.F("String.Download.VersionCount", _allVersions.Count);
        if (VersionList.Items.Count > 0 && VersionList.SelectedIndex < 0) VersionList.SelectedIndex = 0;
    }

    private void ApplyFilter()
    {
        // XAML 解析期间 ComboBox 的 IsSelected 会提前触发事件，此时 VersionList 尚未创建
        if (VersionList is null || VersionTypeCombo is null || SearchBox is null) return;

        IEnumerable<ManifestVersion> query = _allVersions;
        query = _currentType switch
        {
            "release" => query.Where(v => v.Type == "release"),
            "snapshot" => query.Where(v => !string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase)),
            _ => query
        };
        if (!string.IsNullOrWhiteSpace(_search))
            query = query.Where(v => v.Id.Contains(_search, StringComparison.OrdinalIgnoreCase));
        VersionList.ItemsSource = query.Take(400).ToList();
    }

    private void VersionTypeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentType = VersionTypeCombo.SelectedIndex switch
        {
            0 => "release",
            1 => "snapshot",
            _ => "all"
        };
        ApplyFilter();
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        ApplyFilter();
    }

    /// <summary>双击（或点"安装所选"旁的提示）进入版本安装页：在那里定名称和加载器。</summary>
    private void VersionList_OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (VersionList.SelectedItem is not ManifestVersion entry) return;
        var window = Window.GetWindow(this) as MainWindow;
        window?.NavigateToContent(new VersionInstallPage(window!, entry));
    }

    private async void InstallGame_OnClick(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not ManifestVersion entry) return;
        var installTask = Context.Downloads.BeginTask(entry.Id, "game");
        Context.Downloads.MarkTaskReady();
        try
        {
            var json = await Context.ManifestService.FetchVersionJsonAsync(entry);
            if (json is null) { Notice.Error(Lang.T("String.Msg.Download.JsonFailed")); return; }
            var items = await Context.Installer.BuildInstallItemsAsync(json, IncludeAssetsCheck.IsChecked == true);
            if (items.Count == 0)
            {
                Notice.Info(Lang.T("String.Msg.Download.AllExist"));
                if (CreateInstanceCheck.IsChecked == true) CreateInstance(entry.Id, Lang.T("String.Msg.Download.Vanilla"));
                return;
            }
            await StartDownloadsAsync(items, Lang.F("String.Msg.Download.Installing", entry.Id), installTask);

            // 选了加载器就接着装（内联，不再单独开页）
            var kind = (LoaderKindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var finalVersionId = entry.Id;
            var loaderName = Lang.T("String.Msg.Download.Vanilla");

            if (!string.IsNullOrEmpty(kind) && kind != Lang.T("String.Msg.Download.Vanilla"))
            {
                VersionStatusText.Text = Lang.F("String.Msg.Download.InstallingLoader", kind);
                var installed = await InstallLoaderAsync(installTask);
                if (installed is not null) { finalVersionId = installed; loaderName = kind; }
            }

            if (CreateInstanceCheck.IsChecked == true) CreateInstance(finalVersionId, loaderName);
            Context.Downloads.MarkFinished(installTask);
        }
        catch (Exception ex)
        {
            var failedStep = Context.Downloads.Tasks.LastOrDefault()?.SnapshotSteps().LastOrDefault();
            if (failedStep is not null) { failedStep.Status = TaskStepStatus.Failed; failedStep.Detail = ex.Message; }
            Log.Error("Download", Lang.T("String.Msg.Download.InstallFailedTitle"), ex);
            Notice.Error(Lang.T("String.Msg.Download.InstallFailedTitle"), ex.Message);
        }
    }

    public async Task StartDownloadsAsync(List<DownloadItem> items, string title,
        LPML.Core.Download.DownloadTask? task = null)
    {
        // 进度统一由右上角的「任务」中心展示
        // 不清空全局队列：并发装整合包时清空会把对方正在下载的文件抹掉
        if (task is null) Context.Downloads.Enqueue(items);
        else Context.Downloads.Enqueue(items, task);
        await Context.Downloads.RunAsync();
    }

    private void CreateInstance(string versionId, string loader)
    {
        var instance = Context.Instances.Create(versionId, versionId, Context.Settings.Paths.InstanceIsolation);
        if (loader != Lang.T("String.Msg.Download.Vanilla")) { instance.LoaderName = loader; }
        Context.Settings.SelectedInstanceId = instance.Id;
        Context.Save();
    }

    // ── 加载器 ──────────────────────────────────────────────
    private async void VersionList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadLoaderVersionsAsync();

    private async void LoaderKindCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadLoaderVersionsAsync();

    private async Task LoadLoaderVersionsAsync()
    {
        if (!_ready) return;
        var mc = (VersionList.SelectedItem as ManifestVersion)?.Id;
        if (string.IsNullOrEmpty(mc)) return;
        LoaderVersionCombo.Items.Clear();
        try
        {
            var kind = (LoaderKindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            switch (kind)
            {
                case "Fabric":
                    foreach (var v in await Context.Loaders.GetFabricLoadersAsync(mc)) LoaderVersionCombo.Items.Add(v.Version);
                    break;
                case "Quilt":
                    foreach (var v in await Context.Loaders.GetQuiltLoadersAsync()) LoaderVersionCombo.Items.Add(v);
                    break;
                case "Forge":
                    foreach (var v in await Context.Loaders.GetForgeVersionsAsync(mc)) LoaderVersionCombo.Items.Add(v.Version);
                    break;
                case "NeoForge":
                    foreach (var v in await Context.Loaders.GetNeoForgeVersionsAsync(mc)) LoaderVersionCombo.Items.Add(v.Version);
                    break;
                case "OptiFine":
                    foreach (var v in await Context.Loaders.GetOptiFineVersionsAsync(mc)) LoaderVersionCombo.Items.Add(v.Patch);
                    break;
            }
            if (LoaderVersionCombo.Items.Count > 0) LoaderVersionCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Download", Lang.T("String.Msg.Download.LoaderListFailed") + ex.Message);
        }
    }

    /// <summary>安装加载器（由"安装游戏"流程内联调用）。返回最终版本 Id，失败返回 null。</summary>
    private async Task<string?> InstallLoaderAsync(LPML.Core.Download.DownloadTask? task = null)
    {
        var mc = (VersionList.SelectedItem as ManifestVersion)?.Id;
        var version = LoaderVersionCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(mc) || string.IsNullOrEmpty(version)) return null;
        var kind = (LoaderKindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        try
        {
            // 在任务里登记安装步骤，安装选项卡能看到它在做什么
            var step = task is null
                ? Context.Downloads.AddStep(Lang.F("String.Task.StepInstallLoader", kind, version))
                : Context.Downloads.AddStep(Lang.F("String.Task.StepInstallLoader", kind, version), task);
            if (step is not null)
            {
                step.Status = TaskStepStatus.Running;
                step.Detail = mc ?? "";
            }

            string versionId;
            switch (kind)
            {
                case "Fabric":
                    versionId = await Context.Loaders.InstallFabricAsync(mc, version);
                    break;
                case "Quilt":
                    versionId = await Context.Loaders.InstallQuiltAsync(mc, version);
                    break;
                case "Forge":
                case "NeoForge":
                {
                    var java = ResolveJava();
                    if (java is null) { Notice.Error(Lang.T("String.Msg.Download.NeedJavaForge")); return null; }
                    // Forge/NeoForge 要跑 installer（很慢），把输出同步到任务步骤里
                    var progress = new Progress<string>(text =>
                    {
                        Dispatcher.Invoke(() => VersionStatusText.Text = text);
                        if (step is not null) step.Detail = text;
                    });
                    versionId = kind == "Forge"
                        ? await Context.Loaders.InstallForgeAsync(mc, version, java, progress)
                        : await Context.Loaders.InstallNeoForgeAsync(mc, version, null, java, progress);
                    break;
                }
                case "OptiFine":
                {
                    var java = ResolveJava();
                    if (java is null) { Notice.Warn(Lang.T("String.Msg.Download.NeedJavaOptiFine")); return null; }
                    var list = await Context.Loaders.GetOptiFineVersionsAsync(mc);
                    var target = list.FirstOrDefault(x => x.Patch == version);
                    if (target is null) { Notice.Warn(Lang.T("String.Msg.Download.OptiFineNotFound")); return null; }
                    // Forge/NeoForge 要跑 installer（很慢），把输出同步到任务步骤里
                    var progress = new Progress<string>(text =>
                    {
                        Dispatcher.Invoke(() => VersionStatusText.Text = text);
                        if (step is not null) step.Detail = text;
                    });
                    await Context.Loaders.InstallOptiFineAsync(target, java, progress);
                    return mc;
                }
                default:
                    return null;
            }

            var json = Context.Resolver.Load(versionId);
            if (json is not null)
            {
                var items = await Context.Installer.BuildInstallItemsAsync(json, true);
                if (items.Count > 0) await StartDownloadsAsync(items, Lang.F("String.Msg.Download.Installing", kind), task);
            }
            return versionId;
        }
        catch (Exception ex)
        {
            Log.Error("Download", Lang.T("String.Msg.Download.LoaderInstallFailedTitle"), ex);
            VersionStatusText.Text = Lang.T("String.Msg.Download.InstallFailedTitle") + ex.Message;
            return null;
        }
   
        finally
        {
            // 步骤收尾：成功标完成，失败标失败
            var last = Context.Downloads.Tasks.LastOrDefault()?.SnapshotSteps().LastOrDefault();
            if (last is not null && last.Status == TaskStepStatus.Running)
            {
                last.Status = TaskStepStatus.Done;
                last.Detail = Lang.T("String.Core.Task.Done");
            }
        }
 }

    private string? ResolveJava()
    {
        var pool = Context.Java.Detect();
        var java = Context.Java.Recommend(17, pool) ?? Context.Java.Recommend(8, pool);
        return java?.Path ?? Context.Java.FindRuntimeJava("java-runtime-gamma") ?? Context.Java.FindRuntimeJava("jre-legacy");
    }

    // ── 整合包 ──────────────────────────────────────────────
    private void PickPack_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = Lang.T("String.Msg.Download.ModpackFilter"),
            Title = Lang.T("String.Msg.Download.PickModpack")
        };
        if (dialog.ShowDialog() == true) PackPathBox.Text = dialog.FileName;
    }

    /// <summary>导入前确认包内容与限制（尤其 CurseForge 的模组无法自动下载）。</summary>
    private bool ConfirmModpack(ModpackInfo probe)
    {
        var lines = new List<string>
        {
            Lang.F("String.Msg.Download.ModpackFormat", probe.FormatText),
            probe.Summary
        };

        if (probe.ManualMods.Count > 0)
        {
            lines.Add("");
            lines.Add(Lang.F("String.Msg.Download.ModpackCfWarning", probe.ManualMods.Count));
        }

        return Notice.Ask(Lang.T("String.Msg.Download.ModpackConfirmTitle"),
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>列出需要手动下载的 CurseForge 模组，并提供打开项目页。</summary>
    private void ShowManualMods(LPML.Core.Instances.GameInstance instance, List<ManualModEntry> mods)
    {
        var modsDirectory = Context.Instances.InstanceModsDirectory(instance);
        var listText = string.Join(Environment.NewLine,
            mods.Take(20).Select(m => "  " + m.Display));
        var more = mods.Count > 20 ? Environment.NewLine + "  …" : "";

        var text = Lang.F("String.Msg.Download.ModpackCfManual", mods.Count, modsDirectory)
                   + Environment.NewLine + Environment.NewLine + listText + more
                   + Environment.NewLine + Environment.NewLine
                   + Lang.T("String.Msg.Download.ModpackCfOpenWeb");

        if (Notice.Ask(text) && mods.Count > 0)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = mods[0].WebUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Log.Warn("Modpack", ex.Message); }
        }
    }

    // ── 在线整合包（Modrinth）────────────────────────────────

    private List<LPML.Core.Modrinth.ModrinthProject> _packResults = new();

    /// <summary>切到整合包面板时自动拉一批热门整合包。</summary>
    private async Task LoadOnlinePacksAsync()
    {
        if (!_ready || _packResults.Count > 0) return;
        await SearchPacksAsync("");
    }

    private async void SearchPack_OnClick(object sender, RoutedEventArgs e)
        => await SearchPacksAsync(PackSearchBox?.Text?.Trim() ?? "");

    private async Task SearchPacksAsync(string query)
    {
        try
        {
            PackStatusText.Text = Lang.T("String.Download.SearchingPack");
            var response = await Context.Modrinth.SearchAsync(
                query, LPML.Core.Modrinth.ModrinthProjectType.Modpack,
                null, null, 0, 30, "downloads");

            _packResults = response?.Hits ?? new List<LPML.Core.Modrinth.ModrinthProject>();
            PackList.ItemsSource = _packResults.Select(x => new PackRow(x)).ToList();
            PackStatusText.Text = _packResults.Count == 0
                ? Lang.T("String.Download.NoPackFound")
                : Lang.F("String.Download.PackFound", _packResults.Count);
        }
        catch (Exception ex)
        {
            Log.Warn("Modrinth", "Search modpack failed: " + ex.Message);
            PackStatusText.Text = Lang.T("String.Download.PackSearchFailed") + ex.Message;
        }
    }

    /// <summary>双击列表项直接进详情（和原版版本列表一致）。</summary>
    private void PackList_OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenPackDetail();

    /// <summary>点整合包 → 进详情页选版本 / 加载器 / 名称。</summary>
    private void InstallOnlinePack_OnClick(object sender, RoutedEventArgs e)
        => OpenPackDetail();

    private void OpenPackDetail()
    {
        // 没选中时先在列表里选一个，别让用户点了没反应
        if (PackList.SelectedItem is not PackRow row)
        {
            if (PackList.Items.Count == 0)
            {
                PackStatusText.Text = Lang.T("String.Download.NoPackFound");
                return;
            }
            PackList.SelectedIndex = 0;
            row = PackList.SelectedItem as PackRow;
            if (row is null) return;
        }

        var window = Window.GetWindow(this) as MainWindow;
        if (window is null) return;
        window.NavigateToContent(new ModpackInstallPage(window, row.Project));
    }

    private async Task InstallOnlinePackCoreAsync(LPML.Core.Modrinth.ModrinthProject project)
    {

        try
        {
            InstallPackButton.IsEnabled = false;
            PackStatusText.Text = Lang.F("String.Download.PackPreparing", project.Title);

            var versions = await Context.Modrinth.GetVersionsAsync(project.Slug ?? project.Title ?? "");
            var version = versions?.FirstOrDefault();
            if (version is null)
            {
                PackStatusText.Text = Lang.T("String.Download.NoPackVersion");
                return;
            }

            // 整合包一律新建实例，避免覆盖已有存档与模组
            var created = Context.Instances.Create(
                project.Title ?? Lang.T("String.Download.TabModpack"), "", isolated: true);
            var targetDirectory = Context.Instances.InstanceDirectory(created);

            var packTask = Context.Downloads.BeginTask(project.Title ?? Lang.T("String.Download.TabModpack"), "modpack");
            Context.Downloads.MarkTaskReady();

            var item = Context.Modrinth.ToDownloadItem(version,
                LPML.Core.Modrinth.ModrinthProjectType.Modpack, targetDirectory, project.Title);
            item.Section = DownloadSections.PackContent;

            PackStatusText.Text = Lang.F("String.Download.PackDownloading", project.Title);
            await Context.Downloads.RunWithAsync(new[] { item }, packTask);

            var filePath = item.Destination;
            if (!File.Exists(filePath))
            {
                PackStatusText.Text = Lang.T("String.Download.PackDownloadFailed");
                return;
            }

            var prepared = await Context.Modpacks.PrepareAsync(filePath, targetDirectory);
            if (!string.IsNullOrWhiteSpace(prepared.GameVersion))
                created.VersionId = prepared.GameVersion!;

            Context.Modpacks.Extract(filePath, prepared.Extractions, targetDirectory);
            if (prepared.Downloads.Count > 0)
            {
                foreach (var mod in prepared.Downloads) mod.Section = DownloadSections.PackContent;
                PackStatusText.Text = Lang.F("String.Download.PackFetchingFiles", prepared.Downloads.Count);
                await Context.Downloads.RunWithAsync(prepared.Downloads, packTask);
            }

            Context.Instances.Save();
            Context.Downloads.MarkFinished(packTask);
            PackStatusText.Text = Lang.F("String.Download.PackDone", project.Title, created.Name);
        }
        catch (Exception ex)
        {
            Log.Error("Modrinth", "Install online modpack failed", ex);
            PackStatusText.Text = Lang.T("String.Download.PackInstallFailed") + ex.Message;
        }
    }

    /// <summary>在线整合包列表行。</summary>
    private sealed class PackRow
    {
        public LPML.Core.Modrinth.ModrinthProject Project { get; }
        public PackRow(LPML.Core.Modrinth.ModrinthProject project) { Project = project; }
        public string Title => Project.Title ?? Project.Slug ?? "";
        public string SummaryText
        {
            get
            {
                var description = Project.Description ?? "";
                var brief = description.Length > 60 ? description[..60] + "…" : description;
                return $"{Lang.F("String.Content.Downloads", Project.DownloadsText)} · {brief}";
            }
        }
    }

    private async void ImportPack_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PackPathBox.Text) || !File.Exists(PackPathBox.Text)) return;
        var importTask = Context.Downloads.BeginTask(Path.GetFileNameWithoutExtension(PackPathBox.Text), "modpack");
        Context.Downloads.MarkTaskReady();
        try
        {
            // 导入前先看清楚是什么包，别让用户导入一个不知道内容的 zip
            ModpackInfo? probe;
            try { probe = ModpackProbe.Inspect(PackPathBox.Text); }
            catch (Exception ex)
            {
                Notice.Error(Lang.T("String.Msg.Download.ModpackBadZip") + " " + ex.Message);
                return;
            }

            if (!ConfirmModpack(probe)) return;

            var instanceName = string.IsNullOrWhiteSpace(probe.Name)
                ? Path.GetFileNameWithoutExtension(PackPathBox.Text)
                : probe.Name;
            var instance = Context.Instances.Create(instanceName, "", Context.Settings.Paths.InstanceIsolation);
            var target = Context.Instances.InstanceDirectory(instance);

            var (downloads, extractions, gameVersion, manualMods) =
                await Context.Modpacks.PrepareAsync(PackPathBox.Text, target);
            Context.Modpacks.Extract(PackPathBox.Text, extractions, target);

            // CurseForge 包：模组拿不到下载地址，把清单告诉用户
            if (manualMods.Count > 0) ShowManualMods(instance, manualMods);

            if (!string.IsNullOrEmpty(gameVersion))
            {
                instance.VersionId = gameVersion!;
                var manifest = await Context.ManifestService.GetManifestAsync();
                var entry = manifest?.Versions.FirstOrDefault(v => v.Id == gameVersion);
                if (entry is not null)
                {
                    var json = await Context.ManifestService.FetchVersionJsonAsync(entry);
                    if (json is not null)
                    {
                        var gameItems = await Context.Installer.BuildInstallItemsAsync(json, true);
                        // gameItems 已由 BuildInstallItemsAsync 标成「原版内容」；
                        // 这里先把整合包自带的模组标成「整合包内容」，两批才不会混成一组。
                        foreach (var mod in downloads) mod.Section = DownloadSections.PackContent;
                        downloads.AddRange(gameItems);
                    }
                }
            }

            Context.Instances.Save();
            if (downloads.Count > 0) await StartDownloadsAsync(downloads, Lang.T("String.Msg.Download.ImportModpackTitle"), importTask);
            Context.Downloads.MarkFinished(importTask);
            Notice.Info(Lang.T("String.Msg.Download.ModpackImported"));
        }
        catch (Exception ex)
        {
            Log.Error("Download", Lang.T("String.Msg.Download.ModpackFailedTitle"), ex);
            Notice.Error(Lang.T("String.Msg.Common.ImportFailedTitle"), ex.Message);
        }
    }

    private void Pause_OnClick(object sender, RoutedEventArgs e)
    {
        Context.Downloads.Pause();
        Notice.Info(Lang.T("String.Msg.Download.Paused"));
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Context.Downloads.CancelAll();
}
