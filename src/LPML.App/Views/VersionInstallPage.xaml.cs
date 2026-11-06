using System.Windows;
using System.Windows.Controls;
using LPML.Core;
using LPML.Core.Game.Models;
using LPML.Core.Instances;
using static LPML.Core.L;

namespace LPML.App.Views;

/// <summary>
/// 版本安装页：在版本列表点某个版本后进入，
/// 在这里定保存名称、加载器、装到哪个实例文件夹，再开始安装。
/// </summary>
public partial class VersionInstallPage : UserControl
{
    public LauncherContext Context => App.Context;

    private readonly ManifestVersion _entry;
    private readonly MainWindow? _host;
    private bool _ready;

    public VersionInstallPage(MainWindow host, ManifestVersion entry)
    {
        _host = host;
        _entry = entry;
        InitializeComponent();
        _ready = true;

        VersionTitle.Text = entry.Id;
        VersionMeta.Text = $"{entry.Type} · {entry.ReleaseTime}";
        NameBox.Text = entry.Id;

        LoadFolders();
        _ = LoadLoaderVersionsAsync();
    }

    private void LoadFolders()
    {
        var folders = new List<InstanceFolder> { new() { Id = "", Name = T("String.InstanceSettings.FolderDefault") } };
        folders.AddRange(Context.Folders.Folders);
        FolderCombo.ItemsSource = folders;
        FolderCombo.SelectedIndex = 0;
    }

    private void Back_OnClick(object sender, RoutedEventArgs e)
        => _host?.Navigate("download");

    /// <summary>用户手动改过保存名称后，就不再自动追加加载器后缀。</summary>
    private bool _nameEdited;
    private bool _settingName;

    private async void LoaderKindCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadLoaderVersionsAsync();
        ApplyNameSuffix();
    }

    private void NameBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // 程序自己填的不算"用户改过"
        if (_settingName) return;
        _nameEdited = true;
    }

    /// <summary>选了加载器就在名称后补后缀；用户已手动改过就不动。</summary>
    private void ApplyNameSuffix()
    {
        if (!_ready || _nameEdited || NameBox is null) return;

        var kind = (LoaderKindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var vanilla = T("String.Msg.Download.Vanilla");
        var suffix = string.IsNullOrEmpty(kind) || kind == vanilla ? "" : "-" + kind.ToLowerInvariant();

        _settingName = true;
        try { NameBox.Text = _entry.Id + suffix; }
        finally { _settingName = false; }
    }

    private async Task LoadLoaderVersionsAsync()
    {
        if (!_ready) return;
        var mc = _entry.Id;
        var kind = (LoaderKindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        LoaderVersionCombo.Items.Clear();

        if (string.IsNullOrEmpty(kind) || kind == T("String.Msg.Download.Vanilla"))
        {
            LoaderStatusText.Text = T("String.Install.VanillaHint");
            return;
        }

        try
        {
            LoaderStatusText.Text = T("String.Install.LoadingLoader");
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
            if (LoaderVersionCombo.Items.Count > 0)
            {
                LoaderVersionCombo.SelectedIndex = 0;
                LoaderStatusText.Text = "";
            }
            else
            {
                LoaderStatusText.Text = T("String.Install.NoLoaderVersion");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Install", "Load loader versions failed: " + ex.Message);
            LoaderStatusText.Text = T("String.Msg.Download.LoaderListFailed") + ex.Message;
        }
    }

    private async void Install_OnClick(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? _entry.Id : NameBox.Text.Trim();
        var kind = (LoaderKindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var isVanilla = string.IsNullOrEmpty(kind) || kind == T("String.Msg.Download.Vanilla");
        var targetFolder = FolderCombo.SelectedItem as InstanceFolder;

        try
        {
            InstallButton.IsEnabled = false;
            // 先建任务组再准备文件：任务会立刻出现在任务列表里，
            // 否则准备阶段（拉 JSON + 列文件）会有几秒空窗，像是没点上。
            StatusText.Text = T("String.Task.Creating");
            // 保存自己的任务句柄：后面每一批文件都显式归到它名下。
            // 否则并发装别的东西时 BeginTask 会覆盖"当前任务"，文件就串到别的任务里去了。
            var installTask = Context.Downloads.BeginTask(name, "game");
            Context.Downloads.MarkTaskReady();

            // 选了实例文件夹就整套装进去：版本 JSON、库、资源都落到那个文件夹。
            // 以前只把实例"指向"该文件夹、文件却仍下到 .minecraft，
            // 结果启动时去文件夹里找，什么都找不到。
            var folderPath = targetFolder is not null && !string.IsNullOrEmpty(targetFolder.Id)
                ? Context.Folders.Resolve(targetFolder)
                : null;
            var previousDirectory = Context.Paths.BeginGameDirectory(folderPath);
            try
            {
            var json = await Context.ManifestService.FetchVersionJsonAsync(_entry);
            if (json is null)
            {
                StatusText.Text = T("String.Msg.Download.JsonFailed");
                return;
            }

            var items = await Context.Installer.BuildInstallItemsAsync(json, IncludeAssetsCheck.IsChecked == true);
            if (items.Count > 0) await RunDownloadsAsync(items, installTask);

            var finalVersionId = _entry.Id;
            if (!isVanilla)
            {
                StatusText.Text = F("String.Msg.Download.InstallingLoader", kind);
                var installed = await InstallLoaderAsync(_entry.Id, kind);
                if (installed is not null) finalVersionId = installed;
            }

            if (CreateInstanceCheck.IsChecked == true)
            {
                var instance = Context.Instances.Create(name, finalVersionId, Context.Settings.Paths.InstanceIsolation);
                if (!isVanilla) instance.LoaderName = kind;
                if (targetFolder is not null && !string.IsNullOrEmpty(targetFolder.Id))
                {
                    instance.FolderId = targetFolder.Id;
                    Context.Folders.EnsureLayout(targetFolder);
                }
                Context.Settings.SelectedInstanceId = instance.Id;
                Context.Save();
            }

            StatusText.Text = T("String.Install.Done");
            Context.Downloads.MarkFinished(installTask);
            }
            finally
            {
                Context.Paths.EndGameDirectory(previousDirectory);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Install", "Install failed", ex);
            StatusText.Text = T("String.Msg.Download.InstallFailedTitle") + ex.Message;
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private async Task RunDownloadsAsync(List<LPML.Core.Download.DownloadItem> items,
        LPML.Core.Download.DownloadTask task)
    {
        // 不清空全局队列：并发装整合包时，清空会把对方正在下载的文件一起抹掉。
        Context.Downloads.Enqueue(items, task);
        await Context.Downloads.RunAsync();
    }

    private async Task<string?> InstallLoaderAsync(string mc, string kind)
    {
        var version = LoaderVersionCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(version)) return null;

        switch (kind)
        {
            case "Fabric": return await Context.Loaders.InstallFabricAsync(mc, version);
            case "Quilt": return await Context.Loaders.InstallQuiltAsync(mc, version);
            case "Forge":
            case "NeoForge":
            {
                var java = Context.Java.Detect().FirstOrDefault();
                if (java is null)
                {
                    StatusText.Text = T("String.Msg.Download.NeedJavaForge");
                    return null;
                }
                var progress = new Progress<string>(text => Dispatcher.Invoke(() => StatusText.Text = text));
                return kind == "Forge"
                    ? await Context.Loaders.InstallForgeAsync(mc, version, java.Path, progress)
                    : await Context.Loaders.InstallNeoForgeAsync(mc, version, null, java.Path, progress);
            }
            case "OptiFine":
            {
                var java = Context.Java.Detect().FirstOrDefault();
                if (java is null)
                {
                    StatusText.Text = T("String.Msg.Download.NeedJavaOptiFine");
                    return null;
                }
                var list = await Context.Loaders.GetOptiFineVersionsAsync(mc);
                var target = list.FirstOrDefault(x => x.Patch == version);
                if (target is null) return null;
                var progress = new Progress<string>(text => Dispatcher.Invoke(() => StatusText.Text = text));
                await Context.Loaders.InstallOptiFineAsync(target, java.Path, progress);
                return mc;
            }
        }
        return null;
    }
}
