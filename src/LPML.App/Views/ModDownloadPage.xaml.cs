using LPML.App.Services;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Download;
using LPML.Core.Instances;
using LPML.Core.Mods;
using LPML.Core.Modrinth;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;

namespace LPML.App.Views;

/// <summary>
/// 从 Modrinth 在线下载模组到指定实例。
/// 两个入口共用这一页：下载中心 → 下载模组；实例 → 模组 → 下载模组。
/// 后者会预选那个实例，但进页面后仍可改选，最终装到下拉框里选中的实例。
/// </summary>
public partial class ModDownloadPage : UserControl, IRefreshablePage, IParameterizedPage
{
    /// <summary>从模组页跳转时带上要预选的实例 Id，进页面后清空。</summary>
    public static string? PendingInstanceId { get; set; }

    private const int PageSize = 20;

    private readonly LauncherContext _ctx;
    private bool _ready;
    private bool _autoSearched;
    private bool _refreshing;
    private bool _loading;
    private int _offset;
    private int _totalHits;
    private int _searchSeq;
    private int _versionSeq;
    private string? _pendingSelectId;
    private List<ModrinthProject> _results = new();
    private List<ModVersionRow> _versions = new();

    // 按「查询条件 + 页码」缓存已经拉过的页，翻回去不再打源站，
    // 也避免连点下一页时短时间内把整份结果一次性请求完被限流。
    private readonly Dictionary<string, ModrinthSearchResponse> _pageCache = new();

    public ModDownloadPage(LauncherContext context)
    {
        _ctx = context;
        InitializeComponent();
        _ready = true;
        // 首次进页面先拉一批热门模组，和整合包页一致
        Loaded += (_, _) =>
        {
            if (_autoSearched) return;
            _autoSearched = true;
            _ = SearchAsync();
        };
    }

    private GameInstance? CurrentInstance => InstanceCombo.SelectedItem as GameInstance;

    /// <summary>解析二级导航参数：instance=&lt;id&gt;。没有参数就取待预选的实例。</summary>
    public void ApplyArguments(string arguments)
    {
        var id = "";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            foreach (var pair in arguments.Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "instance") id = parts[1];
            }
        }
        if (string.IsNullOrWhiteSpace(id)) id = PendingInstanceId ?? "";
        PendingInstanceId = null;
        if (!string.IsNullOrWhiteSpace(id)) _pendingSelectId = id;
    }

    public void Refresh()
    {
        _refreshing = true;
        InstanceCombo.ItemsSource = null;
        var list = _ctx.InstancesInCurrentFolder();
        InstanceCombo.ItemsSource = list;

        // 优先用跳转带过来的预选，其次保留本页已选，最后才回落到首页挂着的实例
        var currentId = (InstanceCombo.SelectedItem as GameInstance)?.Id;
        var pick = (_pendingSelectId is not null ? list.FirstOrDefault(i => i.Id == _pendingSelectId) : null)
                   ?? (currentId is not null ? list.FirstOrDefault(i => i.Id == currentId) : null)
                   ?? _ctx.Instances.Find(_ctx.Settings.SelectedInstanceId)
                   ?? list.FirstOrDefault();
        _pendingSelectId = null;
        if (pick is not null) InstanceCombo.SelectedItem = pick;
        _refreshing = false;
        UpdateTargetText();
    }

    private void InstanceCombo_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshing) return;
        UpdateTargetText();
        _pageCache.Clear();
        _offset = 0;
        _ = SearchAsync();
    }

    private void UpdateTargetText()
    {
        if (TargetDirText is null) return;
        TargetDirText.Text = CurrentInstance is null
            ? ""
            : _ctx.ModsDirectoryFor(CurrentInstance);
    }

    private void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) NewSearch();
    }

    private void Search_OnClick(object sender, RoutedEventArgs e) => NewSearch();

    private void MatchInstance_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _refreshing) return;
        NewSearch();
    }

    private void NewSearch()
    {
        _pageCache.Clear();
        _offset = 0;
        _totalHits = 0;
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

    /// <summary>
    /// 只按「加载器」做服务端过滤，不把 MC 版本塞进 facets。
    /// Modrinth 的 versions: facet 要求项目索引里恰好有该版本，整合包常用的版本
    /// 经常会让本来兼容的模组整个搜不出来；版本是否可用交给下面的版本列表判断。
    /// </summary>
    private string? CurrentLoader()
    {
        if (MatchInstanceCheck.IsChecked != true || CurrentInstance is null) return null;
        return ResolveLoader(CurrentInstance);
    }

    /// <summary>
    /// 推断实例用的模组加载器。整合包安装后 LoaderName 可能没写全，
    /// 这时看已经装进 mods 目录的模组元数据（fabric.mod.json / mods.toml）。
    /// </summary>
    private string? ResolveLoader(GameInstance instance)
    {
        var direct = LoaderName(instance.LoaderName);
        if (direct is not null) return direct;

        try
        {
            var dir = _ctx.ModsDirectoryFor(instance);
            if (!Directory.Exists(dir)) return null;
            var mods = ModManager.Scan(dir);
            foreach (var mod in mods)
            {
                var l = mod.Loader.ToLowerInvariant();
                if (l.Contains("fabric")) return "fabric";
                if (l.Contains("quilt")) return "quilt";
                if (l.Contains("neoforge")) return "neoforge";
                if (l.Contains("forge")) return "forge";
            }
        }
        catch (IOException) { }
        return null;
    }

    private string CacheKey(string query, string? loader)
        => $"{query}\u0001{loader ?? ""}\u0001{_offset}";

    private async Task SearchAsync()
    {
        if (!_ready || SearchBox is null || _loading) return;
        _loading = true;
        var seq = ++_searchSeq;
        var query = SearchBox.Text.Trim();
        var loader = CurrentLoader();
        var key = CacheKey(query, loader);
        StatusText.Text = Lang.T("String.ModDownload.Searching");
        try
        {
            if (!_pageCache.TryGetValue(key, out var response))
            {
                response = await _ctx.Modrinth.SearchAsync(
                    query, ModrinthProjectType.Mod, null, loader, _offset, PageSize, "downloads");
                _pageCache[key] = response;
            }
            if (seq != _searchSeq) return;

            _results = response.Hits ?? new List<ModrinthProject>();
            _totalHits = response.TotalHits;
            ResultList.ItemsSource = _results.Select(p => new ModRow(p)).ToList();
            VersionCombo.ItemsSource = null;
            _versions.Clear();

            var pages = Math.Max(1, (_totalHits + PageSize - 1) / PageSize);
            PageText.Text = Lang.F("String.ModDownload.PageInfo", _offset / PageSize + 1, pages, _totalHits);
            StatusText.Text = _results.Count == 0
                ? Lang.T("String.ModDownload.NoResults")
                : Lang.F("String.ModDownload.ResultCount", _totalHits);
        }
        catch (Exception ex)
        {
            Log.Warn("Modrinth", "Search mods failed: " + ex.Message);
            StatusText.Text = Lang.F("String.ModDownload.Failed", ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private async void ResultList_OnChanged(object sender, SelectionChangedEventArgs e)
        => await LoadVersionsAsync();

    private async Task LoadVersionsAsync()
    {
        if (!_ready || ResultList.SelectedItem is not ModRow row) return;
        var slug = row.Project.Slug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            StatusText.Text = Lang.T("String.ModDownload.NoVersion");
            return;
        }

        var seq = ++_versionSeq;
        StatusText.Text = Lang.T("String.ModDownload.LoadingVersions");
        try
        {
            // 拉全量再本地排序：服务端同时按 game_versions + loaders 过滤时，
            // 整合包里常见的版本/加载器组合会把所有版本一起滤没。
            var list = await _ctx.Modrinth.GetVersionsAsync(slug!);
            if (seq != _versionSeq) return;

            string? gameVersion = null;
            string? loader = null;
            if (MatchInstanceCheck.IsChecked == true && CurrentInstance is not null)
            {
                gameVersion = CurrentInstance.VersionId;
                loader = CurrentLoader();
            }

            var ordered = list
                .OrderByDescending(v => CompatibleScore(v, gameVersion, loader))
                .ThenByDescending(v => v.DatePublished ?? "")
                .ToList();

            _versions = ordered.Select(v => new ModVersionRow(v, row.Title)).ToList();
            VersionCombo.ItemsSource = _versions;
            if (_versions.Count > 0) VersionCombo.SelectedIndex = 0;
            StatusText.Text = _versions.Count == 0
                ? Lang.T("String.ModDownload.NoVersion")
                : Lang.F("String.ModDownload.VersionCount", _versions.Count);
        }
        catch (Exception ex)
        {
            Log.Warn("Modrinth", "Load mod versions failed: " + ex.Message);
            StatusText.Text = Lang.F("String.ModDownload.Failed", ex.Message);
        }
    }

    private static int CompatibleScore(ModrinthVersion version, string? gameVersion, string? loader)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(gameVersion) && version.GameVersions is not null
            && version.GameVersions.Any(g => string.Equals(g, gameVersion, StringComparison.OrdinalIgnoreCase)))
            score += 2;
        if (!string.IsNullOrWhiteSpace(loader) && version.Loaders is not null
            && version.Loaders.Any(l => string.Equals(l, loader, StringComparison.OrdinalIgnoreCase)))
            score += 1;
        return score;
    }

    private async void Download_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (CurrentInstance is not GameInstance instance)
        {
            Notice.Info(Lang.T("String.ModDownload.NeedInstance"));
            return;
        }
        if (ResultList.SelectedItem is not ModRow project || VersionCombo.SelectedItem is not ModVersionRow picked)
        {
            Notice.Info(Lang.T("String.ModDownload.NeedSelection"));
            return;
        }

        DownloadButton.IsEnabled = false;
        try
        {
            // ToDownloadItem 会自己拼 "mods"，所以传实例的工作目录
            var targetDirectory = _ctx.WorkingDirectoryFor(instance);
            Core.Utils.FileHelper.EnsureDirectory(_ctx.ModsDirectoryFor(instance));

            var item = _ctx.Modrinth.ToDownloadItem(
                picked.Version, ModrinthProjectType.Mod, targetDirectory, project.Title);
            item.Section = DownloadSections.General;

            var task = _ctx.Downloads.BeginTask(project.Title, "mod");
            _ctx.Downloads.MarkTaskReady();
            StatusText.Text = Lang.F("String.ModDownload.Downloading", project.Title);
            await _ctx.Downloads.RunWithAsync(new[] { item }, task);
            _ctx.Downloads.MarkFinished(task);

            if (item.Status is DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.Paused)
            {
                StatusText.Text = Lang.F("String.ModDownload.Failed", item.Error ?? "");
                return;
            }

            StatusText.Text = Lang.F("String.ModDownload.Done", instance.Name);
            Notice.Info(Lang.F("String.ModDownload.Done", instance.Name));
        }
        catch (Exception ex)
        {
            Log.Error("Modrinth", "Download mod failed", ex);
            StatusText.Text = Lang.F("String.ModDownload.Failed", ex.Message);
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private static string? LoaderName(string? loaderName)
        => ShaderLoaderService.ParseLoader(loaderName) switch
        {
            InstanceLoaderKind.Fabric => "fabric",
            InstanceLoaderKind.Forge => "forge",
            InstanceLoaderKind.NeoForge => "neoforge",
            InstanceLoaderKind.Quilt => "quilt",
            _ => null
        };
}

/// <summary>必须是 public 顶层类型：private 嵌套类会让 WPF 绑定取不到属性，下拉框只能显示类型名。</summary>
public sealed class ModRow
{
    public ModrinthProject Project { get; }
    public ModRow(ModrinthProject project) { Project = project; }
    public string Title => Project.Title ?? Project.Slug ?? "";
    public string Summary
    {
        get
        {
            var description = Project.Description ?? "";
            var brief = description.Length > 70 ? description[..70] + "…" : description;
            return $"{Project.DownloadsText} · {brief}";
        }
    }
    public override string ToString() => Title;
}

public sealed class ModVersionRow
{
    public ModrinthVersion Version { get; }
    private readonly string _projectTitle;
    public ModVersionRow(ModrinthVersion version, string projectTitle)
    {
        Version = version;
        _projectTitle = projectTitle;
    }
    public string Label =>
        $"{_projectTitle} · {Version.Name} · {Version.VersionTypeText} · {Version.LoadersText} · {Version.PublishedText}";
    public override string ToString() => Label;
}
