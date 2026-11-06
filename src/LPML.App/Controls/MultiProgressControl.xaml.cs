using LPML.Core.Download;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using LPML.App.Services;

namespace LPML.App.Controls;

/// <summary>
/// 多进度视图：顶部汇总条 + 下方每个文件一条独立进度条（多任务、多线程分段都可见）。
/// </summary>
public partial class MultiProgressControl : UserControl
{
    public ObservableCollection<DownloadItem> Items { get; } = new();

    public MultiProgressControl()
    {
        InitializeComponent();
    }

    public void Reset(IEnumerable<DownloadItem> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        Update(new DownloadAggregate { Total = Items.Count });
    }

    public void Update(DownloadAggregate aggregate)
    {
        TotalBar.Value = aggregate.Progress;
        PercentText.Text = $"{aggregate.Progress * 100:0.0}%";
        SummaryText.Text = aggregate.Total == 0 ? Lang.T("String.Progress.Summary") : aggregate.Summary;
        DetailText.Text = Lang.F("String.Msg.Progress.FailedSummary", aggregate.Failed, aggregate.Elapsed.ToString("mm\\:ss"));
        ActiveCountText.Text = Lang.F("String.Msg.Progress.ActiveSummary", aggregate.Active, aggregate.Total);
    }
}
