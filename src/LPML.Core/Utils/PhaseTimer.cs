using System.Diagnostics;

namespace LPML.Core.Utils;

/// <summary>
/// 阶段计时：用 using 包住一段代码，结束时自动把耗时写进日志。
/// 目的是定位瓶颈到底在哪个环节（下载 / 解压 natives / 写 version json / 构建清单）。
/// </summary>
public sealed class PhaseTimer : IDisposable
{
    private readonly string _phase;
    private readonly Stopwatch _sw;
    private bool _done;

    private PhaseTimer(string phase)
    {
        _phase = phase;
        _sw = Stopwatch.StartNew();
    }

    /// <summary>开始计时一段阶段。</summary>
    public static PhaseTimer Measure(string phase) => new(phase);

    /// <summary>提前结束并记录（不调用则 using 结束时记录）。</summary>
    public long Stop()
    {
        if (_done) return _sw.ElapsedMilliseconds;
        _done = true;
        _sw.Stop();
        Log.Info("Perf", L.F("String.Core.Log.Perf.Phase", _phase, _sw.ElapsedMilliseconds));
        return _sw.ElapsedMilliseconds;
    }

    public void Dispose()
    {
        if (_done) return;
        Stop();
    }
}
