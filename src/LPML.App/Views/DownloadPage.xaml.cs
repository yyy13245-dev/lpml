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

    public async Task StartDownloadsAsync(List<DownloadItem> items, string title,
        LPML.Core.Download.DownloadTask? task = null)
    {
        // 进度统一由右上角的「任务」中心展示
        // 不清空全局队列：并发装整合包时清空会把对方正在下载的文件抹掉
        if (task is null) Context.Downloads.Enqueue(items);
        else Context.Downloads.Enqueue(items, task);
        await Context.Downloads.RunAsync();
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

    /// <summary>列表只负责选中；安装一律进详情页完成。</summary>
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
            var instance = Context.Instances.Create(instanceName, "",
                Context.Settings.Paths.InstanceIsolation, Context.CurrentFolderId);
            if (!string.IsNullOrEmpty(Context.CurrentFolderId))
            {
                instance.FolderId = Context.CurrentFolderId;
                Context.Instances.Save();
            }
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
                        // 原版文件也落到当前文件夹，不装回默认 .minecraft
                        var previousDirectory = Context.Paths.BeginGameDirectory(Context.CurrentFolderDirectory());
                        try
                        {
                            var gameItems = await Context.Installer.BuildInstallItemsAsync(json, true);
                            // gameItems 已由 BuildInstallItemsAsync 标成「原版内容」；
                            // 这里先把整合包自带的模组标成「整合包内容」，两批才不会混成一组。
                            foreach (var mod in downloads) mod.Section = DownloadSections.PackContent;
                            downloads.AddRange(gameItems);
                        }
                        finally { Context.Paths.EndGameDirectory(previousDirectory); }
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
