using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Download;
using LPML.Core.Instances;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace LPML.App.Views;

/// <summary>列表行用公开顶层类，避免 private 嵌套类型在 WPF 绑定里显示成类名。</summary>
public sealed class FolderRow
{
    public InstanceFolder Folder { get; init; } = new();
    public string Name => Folder.Name;
    public string PathText => string.IsNullOrWhiteSpace(Folder.Path)
        ? L.T("String.InstanceSettings.FolderDefault")
        : Folder.Path;
    public bool IsCurrent { get; init; }
    public string Mark => IsCurrent ? L.T("String.Folder.Current") : "";
}

/// <summary>
/// 文件夹页：每个文件夹都是一套独立的游戏目录。
/// 文件夹决定当前能看到哪些实例、名称在哪一档里查重、文件装到哪里，
/// 而不是反过来由某个实例的下拉框把实例"搬"进新文件夹。
/// </summary>
public partial class FoldersPage : UserControl, IRefreshablePage
{
    public LauncherContext Context { get; }
    private bool _ready;

    public FoldersPage(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        _ready = true;
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var current = Context.CurrentFolderId;
        var rows = new List<FolderRow>
        {
            new()
            {
                Folder = new InstanceFolder { Id = "", Name = L.T("String.InstanceSettings.FolderDefault") },
                IsCurrent = string.IsNullOrEmpty(current)
            }
        };
        foreach (var folder in Context.Folders.Folders)
            rows.Add(new FolderRow
            {
                Folder = folder,
                IsCurrent = string.Equals(folder.Id, current, StringComparison.Ordinal)
            });

        FolderList.ItemsSource = rows;
        FolderList.SelectedItem = rows.FirstOrDefault(r => r.IsCurrent) ?? rows[0];
        LoadDetail();
    }

    private FolderRow? Current => FolderList.SelectedItem as FolderRow;

    private string PathOf(InstanceFolder folder)
        => string.IsNullOrEmpty(folder.Id) ? Context.Paths.GameDirectory : Context.Folders.Resolve(folder);

    private void FolderList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        LoadDetail();
    }

    private void LoadDetail()
    {
        var row = Current;
        if (row is null) return;

        var path = PathOf(row.Folder);
        DetailTitle.Text = row.Name;
        DetailPath.Text = path;
        CurrentBadge.Visibility = row.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        SetCurrentButton.IsEnabled = !row.IsCurrent;

        // 磁盘里装过版本但注册表还没登记时先补齐，避免「资源管理器里有、启动器里 0 个」
        var id = row.Folder.Id;
        if (!string.IsNullOrEmpty(id)) Context.RescanFolderInstances(id);
        // 只列真正登记在这个文件夹里的实例，不再把别的实例显示成"这里也有"
        var instances = Context.Instances.Instances
            .Where(i => string.Equals(i.FolderId ?? "", id, StringComparison.Ordinal))
            .ToList();
        DetailInstanceList.ItemsSource = instances;
        if (instances.Count > 0) DetailInstanceList.SelectedIndex = 0;
        InstanceCountText.Text = L.F("String.Folder.InstanceCount", instances.Count);

        // 按物理目录扫描，不读共享 .minecraft 的缓存
        VersionCountText.Text = L.F("String.Folder.VersionCount", Context.InstalledVersionsIn(path).Count);
        StatusText.Text = "";
    }

    /// <summary>切换当前文件夹：会顺带把选中实例重算成本文件夹里的，避免幽灵实例。</summary>
    private void SetCurrent_OnClick(object sender, RoutedEventArgs e)
    {
        var row = Current;
        if (row is null) return;
        Context.CurrentFolderId = row.Folder.Id;
        Refresh();
        Notice.Info(L.F("String.Folder.SetCurrentDone", row.Name));
    }

    /// <summary>新建文件夹：只登记目录并切过去，绝不改动任何现有实例的归属。</summary>
    private void NewFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = L.T("String.InstanceSettings.FolderNew"),
            Width = 520, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this),
            Background = (System.Windows.Media.Brush)FindResource("Brush.Window")
        };

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = L.T("String.InstanceSettings.FolderNewHint"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            Foreground = (System.Windows.Media.Brush)FindResource("Brush.ForegroundSecondary")
        });

        root.Children.Add(new TextBlock { Text = L.T("String.InstanceSettings.FolderName"), Margin = new Thickness(0, 0, 0, 6) });
        var nameBox = new TextBox { Padding = new Thickness(8, 6, 8, 6) };
        root.Children.Add(nameBox);

        root.Children.Add(new TextBlock { Text = L.T("String.InstanceSettings.FolderPath"), Margin = new Thickness(0, 14, 0, 6) });
        var pathGrid = new Grid();
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pathBox = new TextBox { Padding = new Thickness(8, 6, 8, 6) };
        var browse = new Button
        {
            Content = L.T("String.InstanceSettings.DirBrowse"),
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("Button.Base")
        };
        browse.Click += (_, _) =>
        {
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog();
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) pathBox.Text = dlg.SelectedPath;
            }
            catch (Exception ex) { Log.Warn("Folder", "Browse failed: " + ex.Message); }
        };
        pathGrid.Children.Add(pathBox);
        Grid.SetColumn(pathBox, 0);
        pathGrid.Children.Add(browse);
        Grid.SetColumn(browse, 1);
        root.Children.Add(pathGrid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Content = L.T("String.Msg.Common.OK"), Style = (Style)FindResource("Button.Primary"), Padding = new Thickness(18, 7, 18, 7), IsDefault = true };
        var cancel = new Button { Content = L.T("String.Msg.Common.Cancel"), Style = (Style)FindResource("Button.Base"), Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
            {
                Notice.Info(L.T("String.InstanceSettings.FolderNeedBoth"));
                return;
            }
            var folder = Context.Folders.Create(name, path);
            Context.Folders.EnsureLayout(folder);
            // 新建即用：下载中心与实例页会跟着这个文件夹走，但不碰任何现有实例
            Context.CurrentFolderId = folder.Id;
            window.DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        window.Content = root;

        if (window.ShowDialog() == true) Refresh();
    }

    /// <summary>只移除登记，不动磁盘文件；里面还有实例时拒绝，避免留下指向空目录的实例。</summary>
    private void DeleteFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var row = Current;
        if (row is null) return;
        if (string.IsNullOrEmpty(row.Folder.Id))
        {
            Notice.Info(L.T("String.Folder.CannotDeleteDefault"));
            return;
        }
        var count = Context.Instances.Instances.Count(i =>
            string.Equals(i.FolderId ?? "", row.Folder.Id, StringComparison.Ordinal));
        if (count > 0)
        {
            Notice.Info(L.F("String.Folder.CannotDeleteHasInstances", count));
            return;
        }
        if (!Notice.Ask(L.F("String.Folder.ConfirmDelete", row.Name))) return;
        if (row.IsCurrent) Context.CurrentFolderId = "";
        Context.Folders.Delete(row.Folder);
        Refresh();
    }

    private void OpenDir_OnClick(object sender, RoutedEventArgs e)
    {
        var row = Current;
        if (row is null) return;
        try
        {
            var path = PathOf(row.Folder);
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// 给选中实例把游戏文件补齐到这个文件夹。
    /// 目标目录里没有版本 JSON 时会联网补 JSON 再列文件，不会再误判「文件已完整」。
    /// </summary>
    private async void InstallToFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var row = Current;
        if (row is null) return;
        if (DetailInstanceList.SelectedItem is not GameInstance instance)
        {
            Notice.Info(L.T("String.Folder.NeedInstance"));
            return;
        }

        var target = PathOf(row.Folder);
        try
        {
            InstallToFolderButton.IsEnabled = false;
            StatusText.Text = L.F("String.InstanceSettings.InstallingTo", target);
            var progress = new Progress<DownloadAggregate>(agg =>
                StatusText.Text = L.F("String.InstanceSettings.CompletingProgress", agg.Completed, agg.Total));

            var count = await App.GameLauncher.CompleteFilesAsync(
                instance, progress, CancellationToken.None, target);
            StatusText.Text = count == 0
                ? L.T("String.InstanceSettings.InstallUpToDate")
                : L.F("String.InstanceSettings.InstallDone", count, target);
            VersionCountText.Text = L.F("String.Folder.VersionCount", Context.InstalledVersionsIn(target).Count);
        }
        catch (Exception ex)
        {
            Log.Error("Folder", "Install to folder failed", ex);
            StatusText.Text = L.T("String.Msg.Common.Failed") + ex.Message;
        }
        finally
        {
            InstallToFolderButton.IsEnabled = true;
        }
    }
}
