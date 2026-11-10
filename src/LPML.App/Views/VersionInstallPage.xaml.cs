using System.Windows;
using System.Windows.Controls;
using LPML.Core;
using LPML.Core.Download;
using LPML.Core.Game;
using LPML.Core.Game.Models;
using LPML.Core.Instances;
using LPML.Core.Utils;
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
    private Task? _loaderLoadTask;
    private int _loaderLoadSeq;

    /// <summary>用户手动改过保存名称后，就不再自动追加加载器后缀。</summary>
    private bool _nameEdited;
    private bool _settingName;

    public VersionInstallPage(MainWindow host, ManifestVersion entry)
    {
        _host = host;
        _entry = entry;
        InitializeComponent();

        VersionTitle.Text = entry.Id;
        var channel = VersionClassifier.Classify(entry);
        VersionMeta.Text = $"{T(VersionClassifier.ChannelKey(channel))} · {entry.ReleaseTime}";
        if (channel is VersionChannel.Snapshot or VersionChannel.AprilFools)
            UnstableHintText.Visibility = Visibility.Visible;

        // 默认值是程序填的，不能让 NameBox_OnTextChanged 把 _nameEdited 置成 true，
        // 否则之后选加载器时 ApplyNameSuffix 会一直当成"用户改过名字"而跳过。
        _settingName = true;
        try { NameBox.Text = entry.Id; }
        finally { _settingName = false; }

        LoadFolders();
        // 初始化期间的 SelectionChanged 不算用户操作；_ready 放到最后再置位。
        _ready = true;
        _loaderLoadTask = LoadLoaderVersionsAsync();
    }

    private void LoadFolders()
    {
        var folders = new List<InstanceFolder> { new() { Id = "", Name = T("String.InstanceSettings.FolderDefault") } };
        folders.AddRange(Context.Folders.Folders);
        FolderCombo.ItemsSource = folders;
        // 默认跟随「文件夹」页里选中的当前文件夹，不用每次都手动切回去
        var current = Context.CurrentFolderId;
        FolderCombo.SelectedItem = folders.FirstOrDefault(f => string.Equals(f.Id, current, StringComparison.Ordinal))
                                   ?? folders[0];
    }

    /// <summary>从丢失的实例预选加载器类型；找不到对应项就保持原版。</summary>
    public void PreselectLoader(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || LoaderKindCombo is null) return;
        for (var i = 0; i < LoaderKindCombo.Items.Count; i++)
        {
            var text = (LoaderKindCombo.Items[i] as ComboBoxItem)?.Content?.ToString() ?? "";
            if (!string.Equals(text, kind, StringComparison.OrdinalIgnoreCase)) continue;
            LoaderKindCombo.SelectedIndex = i;
            return;
        }
    }

    private void Back_OnClick(object sender, RoutedEventArgs e)
        => _host?.Navigate("download");

    private async void LoaderKindCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        // 保存这次加载任务：点「安装」时先等它完成，避免版本列表还没拉回来
        // 就静默降级成原版安装。
        _loaderLoadTask = LoadLoaderVersionsAsync();
        await _loaderLoadTask;
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
        // 连续切换加载器时，只让最后一次请求的结果写回界面。
        var seq = ++_loaderLoadSeq;
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
            var versions = new List<string>();
            switch (kind)
            {
                case "Fabric":
                    foreach (var v in await Context.Loaders.GetFabricLoadersAsync(mc)) versions.Add(v.Version ?? "");
                    break;
                case "Quilt":
                    foreach (var v in await Context.Loaders.GetQuiltLoadersAsync()) versions.Add(v ?? "");
                    break;
                case "Forge":
                    foreach (var v in await Context.Loaders.GetForgeVersionsAsync(mc)) versions.Add(v.Version ?? "");
                    break;
                case "NeoForge":
                    foreach (var v in await Context.Loaders.GetNeoForgeVersionsAsync(mc)) versions.Add(v.Version ?? "");
                    break;
                case "OptiFine":
                    foreach (var v in await Context.Loaders.GetOptiFineVersionsAsync(mc)) versions.Add(v.Patch ?? "");
                    break;
            }
            if (seq != _loaderLoadSeq) return;
            foreach (var v in versions) LoaderVersionCombo.Items.Add(v);
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
            if (seq != _loaderLoadSeq) return;
            Log.Warn("Install", "Load loader versions failed: " + ex.Message);
            LoaderStatusText.Text = T("String.Msg.Download.LoaderListFailed") + ex.Message;
        }
    }

    private static void StepBegin(TaskStep step) { step.Detail = ""; step.Status = TaskStepStatus.Running; }
    private static void StepDone(TaskStep step, string detail) { step.Detail = detail; step.Status = TaskStepStatus.Done; }
    private static void StepFail(TaskStep step, string detail) { step.Detail = detail; step.Status = TaskStepStatus.Failed; }

    private async void Install_OnClick(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? _entry.Id : NameBox.Text.Trim();
        var kind = (LoaderKindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var isVanilla = string.IsNullOrEmpty(kind) || kind == T("String.Msg.Download.Vanilla");
        var targetFolder = FolderCombo.SelectedItem as InstanceFolder;

        DownloadTask? installTask = null;
        TaskStep? activeStep = null;
        try
        {
            InstallButton.IsEnabled = false;
            // 先建任务组再准备文件：任务会立刻出现在任务列表里，
            // 否则准备阶段（拉 JSON + 列文件）会有几秒空窗，像是没点上。
            StatusText.Text = T("String.Task.Creating");
            // 保存自己的任务句柄：后面每一批文件都显式归到它名下。
            // 否则并发装别的东西时 BeginTask 会覆盖"当前任务"，文件就串到别的任务里去了。
            installTask = Context.Downloads.BeginTask(name, "game");
            Context.Downloads.MarkTaskReady();

            // 安装流程的每一步都登记到这个任务上，点「安装流程」能看到实际做了什么，
            // 原版安装也有步骤，不再是空流程图。
            var stepPrepare = Context.Downloads.AddStep(T("String.Install.Step.Prepare"), installTask);
            activeStep = stepPrepare;
            StepBegin(stepPrepare);

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
                    throw new InvalidOperationException(T("String.Msg.Download.JsonFailed"));
                StepDone(stepPrepare, json.Id ?? _entry.Id);

                var stepVanilla = Context.Downloads.AddStep(T("String.Install.Step.Vanilla"), installTask);
                activeStep = stepVanilla;
                StepBegin(stepVanilla);
                var items = await Context.Installer.BuildInstallItemsAsync(json, IncludeAssetsCheck.IsChecked == true);
                if (items.Count > 0) await RunDownloadsAsync(items, installTask);
                StepDone(stepVanilla, F("String.Install.Step.FileCount", items.Count));

                var finalVersionId = _entry.Id;
                if (!isVanilla)
                {
                    // 版本列表还没拉回来时不能静默当成原版装：等它完成，仍为空就报错。
                    if (_loaderLoadTask is not null) await _loaderLoadTask;
                    if (LoaderVersionCombo.SelectedItem is null)
                        throw new InvalidOperationException(T("String.Install.NoLoaderVersion"));

                    var stepLoader = Context.Downloads.AddStep(T("String.Install.Step.Loader"), installTask);
                    activeStep = stepLoader;
                    StepBegin(stepLoader);
                    StatusText.Text = F("String.Msg.Download.InstallingLoader", kind);
                    var installed = await InstallLoaderAsync(_entry.Id, kind);
                    if (installed is null)
                        throw new InvalidOperationException(T("String.Install.Step.LoaderFailed"));
                    finalVersionId = installed;
                    StepDone(stepLoader, installed);

                    // 加载器 profile 自带的 Maven 库（fabric-loader / sponge-mixin / asm）
                    // 不在原版清单里。只保存 profile JSON 而不下这些库，classpath 上
                    // 有路径、磁盘上却没有文件，JVM 会以 ClassNotFoundException 秒退。
                    // 这里按 profile 的增量库单独列一批，标成「加载器内容」分区；
                    // 不过滤本地已存在的文件，任务里才看得到这批内容。
                    var stepLibs = Context.Downloads.AddStep(T("String.Install.Step.LoaderLibs"), installTask);
                    activeStep = stepLibs;
                    StepBegin(stepLibs);
                    var profile = Json.Load<VersionJson>(Context.Paths.VersionJsonPath(finalVersionId));
                    var loaderItems = profile is null
                        ? new List<DownloadItem>()
                        : Context.Installer.BuildLoaderLibraryItems(profile, json);
                    if (profile is null)
                        Log.Warn("Install", "Loader profile json missing: " + finalVersionId);
                    if (loaderItems.Count > 0)
                    {
                        StatusText.Text = T("String.Msg.Download.LoaderLibraries");
                        await RunDownloadsAsync(loaderItems, installTask);
                    }
                    StepDone(stepLibs, loaderItems.Count > 0
                        ? F("String.Install.Step.FileCount", loaderItems.Count)
                        : T("String.Install.Step.LibsSkipped"));
                }

                if (CreateInstanceCheck.IsChecked == true)
                {
                    var stepInstance = Context.Downloads.AddStep(T("String.Install.Step.Instance"), installTask);
                    activeStep = stepInstance;
                    StepBegin(stepInstance);
                    // 查重只在同一文件夹内做：新文件夹里没有这个名字就不该被旧文件夹挡住。
                    var instance = Context.Instances.Create(name, finalVersionId,
                        Context.Settings.Paths.InstanceIsolation, targetFolder?.Id ?? "");
                    if (!isVanilla) instance.LoaderName = kind;
                    if (targetFolder is not null && !string.IsNullOrEmpty(targetFolder.Id))
                    {
                        instance.FolderId = targetFolder.Id;
                        Context.Folders.EnsureLayout(targetFolder);
                    }
                    Context.Settings.SelectedInstanceId = instance.Id;
                    Context.Save();
                    StepDone(stepInstance, name);
                }

                var stepFinish = Context.Downloads.AddStep(T("String.Install.Step.Finish"), installTask);
                activeStep = stepFinish;
                StepBegin(stepFinish);
                StatusText.Text = T("String.Install.Done");
                StepDone(stepFinish, "");
                Context.Downloads.MarkFinished(installTask);
            }
            finally
            {
                Context.Paths.EndGameDirectory(previousDirectory);
            }
        }
        catch (InvalidOperationException ex)
        {
            // 同一个文件夹里重名属于用户输入冲突，不是安装故障：
            // 不再往日志里丢一条 "Install failed" 加整段堆栈。
            Log.Warn("Install", "Install stopped: " + ex.Message);
            if (activeStep is not null) StepFail(activeStep, ex.Message);
            StatusText.Text = ex.Message;
            if (installTask is not null) Context.Downloads.MarkFinished(installTask);
        }
        catch (Exception ex)
        {
            Log.Error("Install", "Install failed", ex);
            if (activeStep is not null) StepFail(activeStep, ex.Message);
            StatusText.Text = T("String.Msg.Download.InstallFailedTitle") + ex.Message;
            if (installTask is not null) Context.Downloads.MarkFinished(installTask);
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
