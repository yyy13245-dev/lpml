namespace LPML.Core.Download;

/// <summary>全局限速器（令牌桶），单位字节/秒，0 表示不限速。</summary>
public sealed class SpeedLimiter
{
    private readonly object _sync = new();
    private long _limitBytesPerSecond;
    private double _tokens;
    private DateTime _lastTick = DateTime.UtcNow;

    public long LimitBytesPerSecond
    {
        get { lock (_sync) return _limitBytesPerSecond; }
        set { lock (_sync) { _limitBytesPerSecond = value; _tokens = 0; } }
    }

    public async Task ConsumeAsync(int bytes, CancellationToken token)
    {
        long limit;
        lock (_sync) limit = _limitBytesPerSecond;
        if (limit <= 0) return;

        double waitSeconds = 0;
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            _tokens = Math.Min(limit * 2.0, _tokens + elapsed * limit);
            _tokens -= bytes;
            if (_tokens < 0)
            {
                waitSeconds = -_tokens / (double)limit;
                _tokens = 0;
            }
        }

        if (waitSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(waitSeconds, 1.0)), token).ConfigureAwait(false);
    }
}
