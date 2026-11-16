namespace Weixun;

/// <summary>
/// 令牌桶限速器。
///
/// 为什么 SDK 必须自带限速：协议端实测没有任何服务端限流，
/// 客户并发猛发不会收到 API 错误，而是直接被微信风控。
/// </summary>
public sealed class RateLimiter
{
    private readonly int _capacity;
    private readonly double _intervalMs;
    private double _tokens;
    private long _last;
    private readonly object _lock = new();

    public RateLimiter(int capacity, int perWindowMs)
    {
        if (capacity <= 0) throw new ArgumentException("capacity 必须大于 0", nameof(capacity));
        _capacity = capacity;
        _intervalMs = (double)perWindowMs / capacity;
        _tokens = capacity;
        _last = Environment.TickCount64;
    }

    /// <summary>阻塞直到获取一个令牌。</summary>
    public void Acquire()
    {
        while (true)
        {
            lock (_lock)
            {
                var now = Environment.TickCount64;
                _tokens = Math.Min(_capacity, _tokens + (now - _last) / _intervalMs);
                _last = now;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }
                var need = (1 - _tokens) * _intervalMs;
                Monitor.Exit(_lock);
                Thread.Sleep((int)Math.Max(need, 1));
                Monitor.Enter(_lock);
            }
        }
    }
}
