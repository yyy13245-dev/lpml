using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using LPML.App.Windows;

namespace LPML.App.Services;

/// <summary>
/// 悬浮提示：操作超过 0.8 秒才浮「加载中」，多个按出现顺序往下堆；
/// 任务一结束就收起。成功 / 失败提示复用同一张卡，短暂停留后自动消失。
/// </summary>
public static class ToastService
{
    private static readonly List<ToastWindow> _open = new();
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(800);

    /// <summary>包住一个耗时操作：只有真的慢才显示，不会一闪而过。</summary>
    public static async Task<T> RunAsync<T>(string text, Func<Task<T>> work)
    {
        ToastWindow? toast = null;
        var job = work();
        var delay = Task.Delay(Threshold);
        if (await Task.WhenAny(job, delay).ConfigureAwait(true) == delay && !job.IsCompleted)
            toast = Show(text);
        try { return await job.ConfigureAwait(true); }
        finally { toast?.Dismiss(); }
    }

    public static ToastWindow? Show(string text)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return null;
        return dispatcher.Invoke(() =>
        {
            var w = new ToastWindow(text);
            _open.Add(w);
            Reposition();
            w.Show();
            return w;
        });
    }

    public static void Success(string text) => Show(text)?.MarkDone(true);
    public static void Failure(string text) => Show(text)?.MarkDone(false);

    internal static void Remove(ToastWindow w)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => Remove(w)); return; }
        if (_open.Remove(w)) Reposition();
    }

    internal static void RepositionLoaded() => Reposition();

    /// <summary>右上角起排，一张压一张往下堆。</summary>
    private static void Reposition()
    {
        var area = SystemParameters.WorkArea;
        for (var i = 0; i < _open.Count; i++)
        {
            var w = _open[i];
            var width = w.ActualWidth > 0 ? w.ActualWidth : 260;
            w.Left = Math.Max(area.Left, area.Right - 24 - width);
            w.Top = area.Top + 28 + i * 48;
        }
    }
}
