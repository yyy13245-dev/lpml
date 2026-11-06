using LPML.App.Services;
using LPML.Core;
using LPML.Core.Download;
using LPML.Core.Instances;
using LPML.Core.Modrinth;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using LPML.App.Windows;

namespace LPML.App.Views;

/// <summary>
/// 内容浏览页：模组 / 资源包 / 光影 / 整合包，全部走 Modrinth。
/// 不接 CurseForge——它要申请 API key、有调用限制且条款更严，Modrinth 完全够用。
/// </summary>
public partial class ContentPage : UserControl
{
    /// <summary>XAML 解析完成标志：初始化期间控件未必全部创建，事件需跳过。</summary>
    private bool _ready;
    private ModrinthProjectType _type = ModrinthProjectType.Mod;
    private int _offset;
    private const int PageSize = 20;
    private int _totalHits;

    private List<ModrinthProject> _results = new();
    private List<ModrinthVersionGroup> _groups = new();
    private ModrinthProject? _selected;
    private List<ModrinthVersion> _selectedGroupVersions = new();
    private List<ModrinthDependency> _dependencies = new();

    private bool _loading;
    private bool _loaded;

    public ContentPage()
    {
        InitializeComponent();
        _ready = true;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        LoadInstances();
        LoadFilters();
        _ = SearchAsync();
    }

    // ── 实例与筛选 ─────────────────────────────────────────

    private GameInstance? CurrentInstance => InstanceCombo.SelectedItem as GameInstance;

    private void LoadInstances()
    {
        var currentId = (InstanceCombo.SelectedItem as GameInstance)?.Id;
        InstanceCombo.ItemsSource = null;
        var list = App.Context.Instances.Instances.ToList();
        InstanceCombo.ItemsSource = list;
        if (list.Count > 0)
        {
            var selectedId = App.Context.Settings.SelectedInstanceId;
            var picked = list.FirstOrDefault(i => i.Id == currentId)
                         ?? list.FirstOrDefault(i => i.Id == selectedId)
                         ?? list[0];
            InstanceCombo.SelectedItem = picked;
        }
    }

    private void LoadFilters()
    {
        // MC 版本：常用版本 + 当前实例版本置顶
        var versions = new List<string> { "" };
        var instanceVersion = CurrentInstance?.VersionId;
        if (!string.IsNullOrWhiteSpace(instanceVersion)) versions.Add(instanceVersion!);
        versions.AddRange(new[] { "1.21.4", "1.21.3", "1.21.1", "1.20.6", "1.20.4", "1.20.1", "1.19.4", "1.18.2", "1.16.5" });
        GameVersionCombo.ItemsSource = versions.Distinct().ToList();
        if (!string.IsNullOrWhiteSpace(instanceVersion)) GameVersionCombo.SelectedItem = instanceVersion;

        // 加载器：按当前实例的加载器给出候选
        var loaders = new List<string> { "" };
        var loaderKind = ShaderLoaderService.ParseLoader(CurrentInstance?.LoaderName);
        var instanceLoader = loaderKind switch
        {
            InstanceLoaderKind.Fabric => "fabric",
            InstanceLoaderKind.Forge => "forge",
            InstanceLoaderKind.NeoForge => "neoforge",
            InstanceLoaderKind.Quilt => "quilt",
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(instanceLoader)) loaders.Add(instanceLoader);
        loaders.AddRange(new[] { "fabric", "forge", "neoforge", "quilt", "optifine" });
        LoaderCombo.ItemsSource = loaders.Distinct().ToList();
        if (!string.IsNullOrWhiteSpace(instanceLoader)) LoaderCombo.SelectedItem = instanceLoader;

        MatchInstanceCheck.IsChecked = true;
    }

    private void InstanceCombo_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loaded) return;
        LoadFilters();
        ClearDetail();
        _ = SearchAsync();
    }

    private void Filter_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _offset = 0;
        _ = SearchAsync();
    }

    private void MatchInstance_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (MatchInstanceCheck.IsChecked == true && CurrentInstance is not null)
        {
            GameVersionCombo.SelectedItem = CurrentInstance.VersionId;
            var loader = ShaderLoaderService.ParseLoader(CurrentInstance.LoaderName);
            var text = loader switch
            {
                InstanceLoaderKind.Fabric => "fabric",
                InstanceLoaderKind.Forge => "forge",
                InstanceLoaderKind.NeoForge => "neoforge",
                InstanceLoaderKind.Quilt => "quilt",
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(text)) LoaderCombo.SelectedItem = text;
        }
        _offset = 0;
        _ = SearchAsync();
    }

    // ── 类型切换 ───────────────────────────────────────────

    private void TypeTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        if (!int.TryParse(tag, out var index)) return;

        _type = (ModrinthProjectType)index;
        ApplyTabStyle();
        ClearDetail();
        _offset = 0;
        _ = SearchAsync();
    }

    private void ApplyTabStyle()
    {
        var buttons = new[] { TabMod, TabResourcePack, TabShader, TabModpack };
        for (var i = 0; i < buttons.Length; i++)
            buttons[i].Style = (Style)(i == (int)_type
                ? FindResource("TypeTabButtonActive")
                : FindResource("TypeTabButton"));
    }

    // ── 搜索 ───────────────────────────────────────────────

    private void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _offset = 0;
        _ = SearchAsync();
    }

    private void Search_OnClick(object sender, RoutedEventArgs e)
    {
        _offset = 0;
        _ = SearchAsync();
    }

    private void PrevPage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_offset == 0) return;
        _offset = Math.Max(0, _offset - PageSize);
        _ = SearchAsync();
    }

    private void NextPage_OnClick(object sender, RoutedEventArgs e)
    {
        if (_offset + PageSize >= _totalHits) return;
        _offset += PageSize;
        _ = SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_loading) return;
        _loading = true;

        var query = SearchBox.Text?.Trim() ?? "";
        var gameVersion = GameVersionCombo.SelectedItem as string;
        var loader = LoaderCombo.SelectedItem as string;

        try
        {
            var response = await App.Context.Modrinth.SearchAsync(
                query, _type, gameVersion, loader, _offset, PageSize);

            _results = response.Hits ?? new List<ModrinthProject>();
            _totalHits = response.TotalHits;
            ResultList.ItemsSource = _results;
            ResultCountText.Text = Lang.F("String.Content.ResultCount", _totalHits);
            PageText.Text = $"{_offset / PageSize + 1} / {Math.Max(1, (_totalHits + PageSize - 1) / PageSize)}";

            if (_results.Count == 0)
                ResultCountText.Text = Lang.T("String.Content.NoResult");
        }
        catch (Exception ex)
        {
            Log.Error("Modrinth", Lang.T("String.Content.SearchFailed"), ex);
            ResultCountText.Text = Lang.T("String.Content.SearchFailed") + " " + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    // ── 详情 ───────────────────────────────────────────────

    private async void ResultList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        // 初始化保护：XAML 解析期间控件尚未全部创建，此时事件可能提前触发（如 Slider 设 Minimum/Maximum 会引发 ValueChanged）
        if (DependencyLabel is null || DependencyList is null || DetailMeta is null || DetailTitle is null || GameVersionGroupCombo is null) return;
        if (ResultList.SelectedItem is not ModrinthProject project) return;
        _selected = project;
        ClearDetail(keepTitle: true);

        DetailTitle.Text = project.Title;
        DetailMeta.Text = project.Description;

        try
        {
            // 拉取全部版本，按 MC 版本分组（当前实例的排最前并标推荐）
            var versions = await App.Context.Modrinth.GetVersionsAsync(project.Slug);
            var preferred = CurrentInstance?.VersionId;

            _groups = _type == ModrinthProjectType.Modpack
                ? new List<ModrinthVersionGroup>
                  {
                      new() { GameVersion = Lang.T("String.Content.AllVersions"), Versions = versions }
                  }
                : ModrinthClient.GroupByGameVersion(versions, preferred);

            GameVersionGroupCombo.ItemsSource = _groups;
            if (_groups.Count > 0) GameVersionGroupCombo.SelectedIndex = 0;

            // 光影：先检查加载器
            UpdateShaderLoaderBar();

            // 整合包没有依赖
            var showDeps = _type != ModrinthProjectType.Modpack;
            DependencyLabel.Visibility = showDeps ? Visibility.Visible : Visibility.Collapsed;
            DependencyList.Visibility = showDeps ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error("Modrinth", Lang.T("String.Content.LoadFailed"), ex);
            DetailMeta.Text = Lang.T("String.Content.LoadFailed") + " " + ex.Message;
        }
    }

    private void ClearDetail(bool keepTitle = false)
    {
        _groups = new();
        _selectedGroupVersions = new();
        _dependencies = new();
        GameVersionGroupCombo.ItemsSource = null;
        VersionList.ItemsSource = null;
        DependencyList.ItemsSource = null;
        if (!keepTitle) DetailTitle.Text = "";
        DetailEmptyHint.Visibility = keepTitle ? Visibility.Collapsed : Visibility.Visible;
        ShaderLoaderBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>切换 MC 版本分组 → 列出该 MC 版本下这个模组的所有版本。</summary>
    private void GameVersionGroup_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初始化保护：XAML 解析期间控件尚未全部创建，此时事件可能提前触发（如 Slider 设 Minimum/Maximum 会引发 ValueChanged）
        if (VersionList is null) return;
        if (GameVersionGroupCombo.SelectedItem is not ModrinthVersionGroup group) return;

        // 版本内按发布时间倒序
        _selectedGroupVersions = group.Versions
            .OrderByDescending(v => v.DatePublished ?? "")
            .ToList();
        VersionList.ItemsSource = _selectedGroupVersions;
        if (_selectedGroupVersions.Count > 0) VersionList.SelectedIndex = 0;
    }

    private void VersionList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初始化保护：XAML 解析期间控件尚未全部创建，此时事件可能提前触发（如 Slider 设 Minimum/Maximum 会引发 ValueChanged）
        if (DependencyLabel is null || DependencyList is null) return;
        if (VersionList.SelectedItem is not ModrinthVersion version) return;

        // 整合包没有依赖，不展示
        _dependencies = _type == ModrinthProjectType.Modpack
            ? new List<ModrinthDependency>()
            : (version.Dependencies ?? new List<ModrinthDependency>());
        DependencyList.ItemsSource = _dependencies;

        // 依赖只给 id，异步补名字（失败就显示 id，不阻塞）
        _ = ResolveDependencyNamesAsync(_dependencies);
        DependencyLabel.Visibility = _type == ModrinthProjectType.Modpack
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (_selected is null) return;
        DetailMeta.Text = Lang.F("String.Content.DetailMeta",
            _selected.DownloadsText, version.VersionTypeText, version.PublishedText, version.LoadersText);
    }

    private void DependencyList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 点依赖 → 跳到那个项目
        if (DependencyList.SelectedItem is not ModrinthDependency dependency) return;
        if (string.IsNullOrWhiteSpace(dependency.ProjectId)) return;
        _ = OpenDependencyAsync(dependency.ProjectId!);
    }

    private async Task ResolveDependencyNamesAsync(List<ModrinthDependency> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.ProjectId)) continue;
            if (!string.IsNullOrWhiteSpace(dependency.Name)) continue;
            try
            {
                var detail = await App.Context.Modrinth.GetDependencyAsync(dependency.ProjectId!);
                if (detail is not null) dependency.Name = detail.Title;
            }
            catch (IOException) { }
        }
    }

    private async Task OpenDependencyAsync(string projectId)
    {
        try
        {
            var detail = await App.Context.Modrinth.GetDependencyAsync(projectId);
            if (detail is null) return;

            SearchBox.Text = "";
            var project = new ModrinthProject
            {
                Slug = detail.Slug,
                Title = detail.Title,
                Description = detail.Description,
                ProjectType = detail.ProjectType,
                IconUrl = detail.IconUrl,
                Downloads = detail.Downloads,
                Follows = detail.Follows
            };
            _results = new List<ModrinthProject> { project };
            ResultList.ItemsSource = _results;
            ResultList.SelectedItem = project;
        }
        catch (Exception ex)
        {
            Log.Error("Modrinth", Lang.T("String.Content.LoadFailed"), ex);
        }
    }

    // ── 光影加载器 ─────────────────────────────────────────

    private void UpdateShaderLoaderBar()
    {
        if (_type != ModrinthProjectType.Shader) { ShaderLoaderBar.Visibility = Visibility.Collapsed; return; }

        var instance = CurrentInstance;
        if (instance is null) { ShaderLoaderBar.Visibility = Visibility.Collapsed; return; }

        var modsDir = App.Context.Instances.InstanceModsDirectory(instance);
        var installed = ShaderLoaderService.DetectInstalled(modsDir);

        ShaderLoaderBar.Visibility = Visibility.Visible;
        if (installed != ShaderLoaderKind.None)
        {
            ShaderLoaderText.Text = Lang.F("String.Content.Shader.Ready",
                ShaderLoaderService.DisplayName(installed));
            ShaderLoaderAction.Visibility = Visibility.Collapsed;
            return;
        }

        var warning = ShaderLoaderService.Check(instance, modsDir);
        var recommended = ShaderLoaderService.Recommend(
            ShaderLoaderService.ParseLoader(instance.LoaderName));

        ShaderLoaderText.Text = warning ?? Lang.T("String.Content.Shader.NeedLoaderFirst");
        ShaderLoaderAction.Visibility = Visibility.Visible;
        ShaderLoaderAction.Content = Lang.F("String.Content.Shader.FindLoader",
            ShaderLoaderService.DisplayName(recommended));
        ShaderLoaderAction.Tag = ShaderLoaderService.ModrinthSlug(recommended);
    }

    private void ShaderLoaderAction_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string slug) return;
        // 直接用 slug 打开搜索：Modrinth 搜索支持 slug 精确命中
        SearchBox.Text = slug;
        _offset = 0;
        _ = SearchAsync();
    }

    // ── 安装 ───────────────────────────────────────────────

    private async void Install_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) { Notice.Info(Lang.T("String.Content.SelectFirst")); return; }
        if (VersionList.SelectedItem is not ModrinthVersion version) { Notice.Info(Lang.T("String.Content.PickVersion")); return; }

        var instance = CurrentInstance;
        if (instance is null) { Notice.Warn(Lang.T("String.Content.NeedInstance")); return; }

        try
        {
            var instanceDirectory = App.Context.Instances.InstanceDirectory(instance);
            var item = App.Context.Modrinth.ToDownloadItem(version, _type, instanceDirectory, _selected.Title);

            // 整合包：下载后按 .mrpack 导入
            if (_type == ModrinthProjectType.Modpack)
            {
                await InstallModpackAsync(instance, version, item);
                return;
            }

            var contentTask = App.Context.Downloads.BeginTask(_selected?.Title ?? item.Name, "content");
            App.Context.Downloads.Enqueue(item, contentTask);
            _ = App.Context.Downloads.RunAsync();
            Notice.Info(Lang.F("String.Content.InstallStarted", _selected.Title));
        }
        catch (Exception ex)
        {
            Log.Error("Modrinth", Lang.T("String.Content.InstallFailed"), ex);
            Notice.Error(ex.Message);
        }
    }

    private async Task InstallModpackAsync(GameInstance instance, ModrinthVersion version, DownloadItem item)
    {
        try
        {
            var packTask = App.Context.Downloads.BeginTask(_selected?.Title ?? item.Name, "modpack");
            item.Section = DownloadSections.PackContent;
            await App.Context.Downloads.RunWithAsync(new[] { item }, packTask);

            var filePath = item.Destination;
            if (!File.Exists(filePath))
            {
                Notice.Error(Lang.T("String.Content.InstallFailed"));
                return;
            }

            // 整合包一律新建实例，避免覆盖用户已有的存档与模组
            var created = App.Context.Instances.Create(
                _selected?.Title ?? Lang.T("String.Content.Type.Modpack"),
                instance.VersionId, isolated: true);
            var targetDirectory = App.Context.Instances.InstanceDirectory(created);

            var prepared = await App.Context.Modpacks.PrepareAsync(filePath, targetDirectory);

            if (!string.IsNullOrWhiteSpace(prepared.GameVersion))
                created.VersionId = prepared.GameVersion!;

            App.Context.Modpacks.Extract(filePath, prepared.Extractions, targetDirectory);
            foreach (var mod in prepared.Downloads) mod.Section = DownloadSections.PackContent;
            App.Context.Downloads.Enqueue(prepared.Downloads, packTask);
            App.Context.Instances.Save();

            LoadInstances();
            InstanceCombo.SelectedItem = created;

            _ = App.Context.Downloads.RunAsync();
            Notice.Info(Lang.F("String.Content.ModpackStarted", _selected?.Title ?? ""));
        }
        catch (Exception ex)
        {
            Log.Error("Modrinth", Lang.T("String.Content.InstallFailed"), ex);
            Notice.Error(ex.Message);
        }
    }

    // ── 跳转网页 ───────────────────────────────────────────

    private void OpenWeb_OnClick(object sender, RoutedEventArgs e)
    {
        var url = "https://modrinth.com/" + _type.ApiName() + "s";
        TryOpenUrl(url);
    }

    private void OpenProjectWeb_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        TryOpenUrl(_selected.WebUrl);
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Content", Lang.T("String.Content.OpenWebFailed") + " " + ex.Message);
        }
    }
}
