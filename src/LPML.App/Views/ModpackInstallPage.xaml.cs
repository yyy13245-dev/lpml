using System.Windows;
using System.Windows.Controls;
using LPML.Core;
using LPML.Core.Download;
using LPML.Core.Modrinth;
using static LPML.Core.L;
using System.IO;
using LPML.App.Windows;

namespace LPML.App.Views;

/// <summary>
/// 整合包安装页：选版本、选加载器（只能选作者提供的）、填保存名，确认后开始安装。
/// </summary>
public partial class ModpackInstallPage : UserControl
{
    public LauncherContext Context => App.Context;

    private readonly ModrinthProject _project;
    private readonly MainWindow? _host;
    private List<ModrinthVersion> _versions = new();
    private List<string> _gameVersions = new();
    private bool _ready;

    public ModpackInstallPage(MainWindow host, ModrinthProject project)
    {
        _host = host;
        _project = project;
        InitializeComponent();
        _ready = true;

        PackTitle.Text = project.Title ?? project.Slug ?? "";
        PackMeta.Text = project.Description ?? "";
        NameBox.Text = project.Title ?? project.Slug ?? "";

        _ = LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            StatusText.Text = T("String.Download.SearchingPack");
            _versions = await Context.Modrinth.GetVersionsAsync(_project.Slug ?? _project.Title ?? "");
            if (_versions.Count == 0)
            {
                StatusText.Text = T("String.Download.NoPackVersion");
                return;
            }

            // 汇总这个整合包支持的所有 MC 版本（新的在前）
            _gameVersions = _versions
                .SelectMany(v => v.GameVersions ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(x => x, StringComparer.Ordinal)
                .ToList();

            GameVersionCombo.ItemsSource = _gameVersions;
            if (_gameVersions.Count > 0)
            {
                GameVersionCombo.SelectedIndex = 0;   // 触发下面的筛选
                StatusText.Text = "";
            }
            else
            {
                // 没标 MC 版本（少见）：直接列全部
                VersionList.ItemsSource = _versions;
                VersionList.SelectedIndex = 0;
                StatusText.Text = "";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Modrinth", "Load modpack versions failed: " + ex.Message);
            StatusText.Text = T("String.Download.PackSearchFailed") + ex.Message;
        }
    }

    /// <summary>选了 MC 版本 → 只列出支持它的整合包版本。</summary>
    private void GameVersionCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || VersionList is null) return;
        if (GameVersionCombo.SelectedItem is not string gameVersion) return;

        var matched = _versions
            .Where(v => v.GameVersions?.Any(g => string.Equals(g, gameVersion, StringComparison.Ordinal)) == true)
            .ToList();

        VersionList.ItemsSource = matched;
        if (matched.Count > 0) VersionList.SelectedIndex = 0;   // 触发加载器刷新
    }

    private void Back_OnClick(object sender, RoutedEventArgs e)
        => _host?.Navigate("download");

    /// <summary>选版本后刷新该版本支持的加载器（只列作者打包时带的那些）。</summary>
    private void VersionList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LoaderCombo is null || LoaderHintText is null) return;
        LoaderCombo.Items.Clear();
        var loaders = (VersionList.SelectedItem as ModrinthVersion)?.Loaders ?? new List<string>();
        foreach (var loader in loaders) LoaderCombo.Items.Add(loader);

        if (LoaderCombo.Items.Count > 0)
        {
            LoaderCombo.SelectedIndex = 0;
            LoaderHintText.Text = F("String.Modpack.LoaderFromAuthor", loaders.Count);
        }
        else
        {
            LoaderHintText.Text = T("String.Modpack.NoLoader");
        }
    }

    private void NameBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || NameWarning is null) return;
        NameWarning.Text = Context.Instances.NameExists(NameBox.Text, Context.CurrentFolderId)
            ? T("String.Modpack.NameDuplicate")
            : "";
    }

    private async void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();

        // 名称查重
        if (Context.Instances.NameExists(name, Context.CurrentFolderId))
        {
            Notice.Warn(T("String.Modpack.NameDuplicate"));
            return;
        }

        var version = VersionList.SelectedItem as ModrinthVersion;
        if (version is null)
        {
            Notice.Info(T("String.Modpack.NeedVersion"));
            return;
        }

        try
        {
            ConfirmButton.IsEnabled = false;
            StatusText.Text = F("String.Download.PackPreparing", _project.Title);

            var created = Context.Instances.Create(name, "", isolated: true,
                duplicateScopeFolderId: Context.CurrentFolderId);
            var targetDirectory = Context.Instances.InstanceDirectory(created);

            // 整合包会分多批下载（本体包 → 原版 JSON/库 → 模组），
            // 每批都要显式归到同一个任务，否则并发装别的实例时会串台。
            var packTask = Context.Downloads.BeginTask(name, "modpack");
            Context.Downloads.MarkTaskReady();
            var item = Context.Modrinth.ToDownloadItem(version, ModrinthProjectType.Modpack, targetDirectory, name);
            item.Section = DownloadSections.PackContent;

            var packStep = Context.Downloads.AddStep(T("String.Task.StepDownloadPack"), packTask);
            if (packStep is not null) { packStep.Status = TaskStepStatus.Running; packStep.Detail = item.Name; }
            StatusText.Text = F("String.Download.PackDownloading", _project.Title);
            await Context.Downloads.RunWithAsync(new[] { item }, packTask);
            if (packStep is not null)
            {
                packStep.Status = TaskStepStatus.Done;
                packStep.Detail = F("String.Task.StepFileCount", 1);
            }

            var filePath = item.Destination;
            if (!File.Exists(filePath))
            {
                StatusText.Text = T("String.Download.PackDownloadFailed");
                return;
            }

            var prepared = await Context.Modpacks.PrepareAsync(filePath, targetDirectory);

            // 优先用你在下拉里选的 MC 版本——那是明确意图；
            // 没有选（整合包没标版本）才回落到整合包自己声明的。
            var gameVersion = GameVersionCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(gameVersion)) gameVersion = prepared.GameVersion;
            if (string.IsNullOrWhiteSpace(gameVersion) && version.GameVersions is { Count: > 0 })
                gameVersion = version.GameVersions.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
            if (!string.IsNullOrWhiteSpace(gameVersion))
                created.VersionId = gameVersion!;

            // 整合包里只有模组和 overrides，MC 本体（版本 JSON / 客户端 jar / 库）不在包里。
            // 必须单独装一遍，否则实例的 VersionId 指向 26.3，
            // versions\26.3\26.3.json 却从未存在过，启动一定卡在"找不到版本 JSON"。
            if (!string.IsNullOrWhiteSpace(gameVersion))
                await InstallVanillaBaseAsync(created, gameVersion!, packTask);

            // 记录作者指定的加载器
            if (LoaderCombo.SelectedItem is string loader && !string.IsNullOrWhiteSpace(loader))
                created.LoaderName = loader;

            var extractStep = Context.Downloads.AddStep(T("String.Task.StepExtract"), packTask);
            if (extractStep is not null) { extractStep.Status = TaskStepStatus.Running; extractStep.Detail = F("String.Task.StepFileCount", prepared.Extractions.Count); }
            Context.Modpacks.Extract(filePath, prepared.Extractions, targetDirectory);
            if (extractStep is not null) extractStep.Status = TaskStepStatus.Done;
            if (prepared.Downloads.Count > 0)
            {
                foreach (var mod in prepared.Downloads) mod.Section = DownloadSections.PackContent;
                var modsStep = Context.Downloads.AddStep(T("String.Task.StepDownloadMods"), packTask);
                if (modsStep is not null)
                {
                    modsStep.Status = TaskStepStatus.Running;
                    modsStep.Detail = F("String.Task.StepFileCount", prepared.Downloads.Count);
                }
                StatusText.Text = F("String.Download.PackFetchingFiles", prepared.Downloads.Count);
                await Context.Downloads.RunWithAsync(prepared.Downloads, packTask);
                if (modsStep is not null) modsStep.Status = TaskStepStatus.Done;
            }

            Context.Instances.Save();
            Context.Downloads.MarkFinished(packTask);
            StatusText.Text = F("String.Download.PackDone", _project.Title, created.Name);
        }
        catch (InvalidOperationException ex)
        {
            // 重名等：直接提示换名字
            Log.Warn("Modpack", "Create instance failed: " + ex.Message);
            Notice.Warn(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Modpack", "Install modpack failed", ex);
            StatusText.Text = T("String.Download.PackInstallFailed") + ex.Message;
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 装整合包所需的原版 MC。整合包自带的东西里没有游戏本体，
    /// 版本 JSON、客户端 jar、依赖库都得从版本清单单独装。
    /// 装到共享游戏目录（一般是 .minecraft），实例的隔离目录只放存档和模组。
    /// </summary>
    private async Task InstallVanillaBaseAsync(LPML.Core.Instances.GameInstance instance, string gameVersion,
        LPML.Core.Download.DownloadTask packTask)
    {
        var step = Context.Downloads.AddStep(T("String.Task.StepVanilla"), packTask);
        try
        {
            if (step is not null) step.Status = TaskStepStatus.Running;

            var manifest = await Context.ManifestService.GetManifestAsync();
            var entry = manifest?.Versions.FirstOrDefault(
                v => string.Equals(v.Id, gameVersion, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                // 清单里没有（比如极新的快照还没同步）就跳过，不阻断整合包安装
                Log.Warn("Modpack", "版本清单里找不到 " + gameVersion + "，跳过原版安装");
                if (step is not null)
                {
                    step.Status = TaskStepStatus.Done;
                    step.Detail = T("String.Task.StepSkipped");
                }
                return;
            }

            var previous = Context.Paths.BeginGameDirectory(Context.SharedGameDirectoryFor(instance));
            try
            {
                var json = await Context.ManifestService.FetchVersionJsonAsync(entry);
                if (json is null)
                {
                    Log.Warn("Modpack", "获取 " + gameVersion + " 版本 JSON 失败");
                    if (step is not null) step.Status = TaskStepStatus.Failed;
                    return;
                }

                if (step is not null) step.Detail = F("String.Task.StepPreparing", gameVersion);

                var items = await Context.Installer.BuildInstallItemsAsync(json, true);
                if (items.Count > 0)
                {
                    // 步骤里实时反映装到哪了，否则"安装选项卡"只剩一个干转的圈
                    EventHandler<DownloadAggregate>? onProgress = null;
                    if (step is not null)
                    {
                        onProgress = (_, agg) => step.Detail =
                            F("String.Task.StepVanillaProgress", gameVersion, agg.Completed, agg.Total);
                        Context.Downloads.ProgressChanged += onProgress;
                    }

                    try
                    {
                        StatusText.Text = F("String.Download.VanillaForPack", gameVersion, items.Count);
                        // 版本 JSON、客户端 jar、依赖库、资源都进下载选项卡，逐文件可见
                        await Context.Downloads.RunItemsAsync(items, packTask);
                    }
                    finally
                    {
                        if (onProgress is not null) Context.Downloads.ProgressChanged -= onProgress;
                    }
                }

                if (step is not null)
                {
                    step.Status = TaskStepStatus.Done;
                    step.Detail = F("String.Task.StepVanillaDone", gameVersion, items.Count);
                }
            }
            finally
            {
                Context.Paths.EndGameDirectory(previous);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户点了取消，不是故障：别记成 Error，也别显示成失败
            Log.Info("Modpack", "原版安装已取消");
            if (step is not null)
            {
                step.Status = TaskStepStatus.Pending;
                step.Detail = T("String.Task.StepCancelled");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Modpack", "Install vanilla base failed", ex);
            if (step is not null)
            {
                step.Status = TaskStepStatus.Failed;
                step.Detail = ex.Message;
            }
        }
    }
}
