using System.Collections.Generic;

namespace LPML.Core.Download;

/// <summary>
/// 源健康表：记录哪些域名近期有效吞吐偏低，以及哪些域名已经完全不可达。
/// 短降级带冷却（每个 5 秒采样窗口递减一次），一次抖动不会永久拉黑某个源；
/// 长熔断用于 DNS 解析失败、SSL 握手失败、连续 403 这类"换几次都救不回来"的故障。
/// </summary>
public sealed class SourceHealth
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _cooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _broken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标记一个域名暂时不优先，冷却 6 个采样窗口（约 30 秒）。</summary>
    public void MarkDegraded(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        lock (_sync) _cooldown[host] = 6;
    }

    /// <summary>
    /// 主机不可达时拉黑一段时间。后续排队文件会整体绕开它，
    /// 不会让几千个文件各自再去撞一次 DNS 超时。
    /// </summary>
    public void MarkBroken(string host, int seconds = 300)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        lock (_sync) _broken[host] = DateTime.UtcNow.AddSeconds(Math.Max(30, seconds));
    }

    public bool IsDegraded(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        lock (_sync)
        {
            if (_broken.TryGetValue(host, out var until))
            {
                if (until > DateTime.UtcNow) return true;
                _broken.Remove(host);
            }
            return _cooldown.TryGetValue(host, out var left) && left > 0;
        }
    }

    /// <summary>每个采样窗口调用一次，冷却到期自动恢复。</summary>
    public void Tick()
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var brokenHosts = new List<string>(_broken.Keys);
            foreach (var host in brokenHosts)
                if (_broken[host] <= now) _broken.Remove(host);

            var hosts = new List<string>(_cooldown.Keys);
            foreach (var host in hosts)
            {
                var left = _cooldown[host];
                if (left <= 1) _cooldown.Remove(host);
                else _cooldown[host] = left - 1;
            }
        }
    }

    public void Clear()
    {
        lock (_sync) { _cooldown.Clear(); _broken.Clear(); }
    }
}
