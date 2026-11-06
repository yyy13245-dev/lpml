namespace LPML.Core.Download;

/// <summary>
/// 全局"连接预算"：所有正在下载的文件（含分段）占用的连接总数不超过上限。
///
/// 为什么需要它：
/// 文件级并发（Semaphore）只限制"同时下几个文件"，但一个大文件内部又会开 8 个分段，
/// 于是实际连接数 = 文件数 × 段数，远超设定值，反而更容易触发镜像限流。
///
/// 有了预算后：
/// - 小文件多时，每个文件只占 1 条连接，连接数被文件级并发吃掉；
/// - 队列里只剩一两个大文件时，空闲预算全部给它们，段数自动往上加（跑满带宽）；
/// - 总量始终不超过上限，也就是"原来的限制"。
/// </summary>
public sealed class StreamBudget
{
    private readonly object _gate = new();
    private int _used;
    private int _max = 32;

    /// <summary>连接总数上限（跟随文件级并发设置）。</summary>
    public int Max
    {
        get { lock (_gate) return _max; }
        set { lock (_gate) _max = Math.Max(1, value); }
    }

    /// <summary>当前已占用的连接数。</summary>
    public int Used
    {
        get { lock (_gate) return _used; }
    }

    public int Free
    {
        get { lock (_gate) return Math.Max(0, _max - _used); }
    }

    /// <summary>
    /// 申请连接。返回实际拿到的条数（至少 1 条，保证不会卡死）。
    /// </summary>
    /// <param name="desired">想要的连接数。</param>
    /// <param name="boostCap">队列空闲时最多能放宽到多少（大文件独占带宽时用）。</param>
    public int Acquire(int desired, int boostCap = 0)
    {
        desired = Math.Max(1, desired);
        lock (_gate)
        {
            var free = Math.Max(1, _max - _used);

            // 剩余预算比"想要的多"时说明别人没在用，可以放宽：
            // 这正是"只剩一个大文件了就别让它单线程慢慢下"的场景。
            var wanted = desired;
            if (boostCap > desired && free > desired)
                wanted = Math.Min(boostCap, free);

            var granted = Math.Clamp(Math.Min(wanted, free), 1, _max);
            _used += granted;
            return granted;
        }
    }

    /// <summary>归还连接（下载结束或失败时调用，必须和 Acquire 配对）。</summary>
    public void Release(int count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            _used -= count;
            if (_used < 0) _used = 0;
        }
    }

    public void Reset()
    {
        lock (_gate) _used = 0;
    }
}
