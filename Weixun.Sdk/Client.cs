using System.Text;
using System.Text.Json;

namespace Weixun;

/// <summary>
/// 微寻微信协议 API 的 .NET 客户端。
///
/// 设计要点（均来自协议端实测）：
///  - license key 一律走 query 参数 key
///  - HTTP 状态码几乎恒为 200，成败看响应体 Code
///  - 64 位 ID 用 string（System.Text.Json 默认会保留 long，但对外统一字符串）
///  - 协议端完全不限流，SDK 默认 40 条/分钟
/// </summary>
public sealed class WeixunClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly RateLimiter? _limiter;

    public string BaseUrl { get; }
    public MessageService Message { get; }
    public LoginService Login { get; }
    public UploadService Upload { get; }
    public TimeSpan Timeout { get; }

    public WeixunClient(string baseUrl, string licenseKey, int rateLimitPerMinute = 40)
        : this(baseUrl, licenseKey, rateLimitPerMinute, TimeSpan.FromSeconds(30)) { }

    public WeixunClient(string baseUrl, string licenseKey, int rateLimitPerMinute, TimeSpan timeout)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        LicenseKey = licenseKey;
        Timeout = timeout;
        _http = new HttpClient { Timeout = timeout };
        _limiter = rateLimitPerMinute > 0 ? new RateLimiter(rateLimitPerMinute, 60_000) : null;
        Message = new MessageService(this);
        Login = new LoginService(this);
        Upload = new UploadService(this);
    }

    internal string LicenseKey { get; }

    /// <summary>WebSocket 地址（供上层建流使用）。</summary>
    public string StreamUrl() => StreamFrame.WsUrl(BaseUrl, LicenseKey);

    /// <summary>脱敏后的 key，用于日志。</summary>
    public string KeyMasked =>
        LicenseKey.Length <= 8 ? "****" : $"{LicenseKey[..4]}****{LicenseKey[^4..]}";

    /// <summary>内部：统一调用出口，返回 Data 元素。</summary>
    internal async Task<JsonElement> CallAsync(
        HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        _limiter?.Acquire();

        var sep = path.Contains('?') ? "&" : "?";
        var url = $"{BaseUrl}{path}{sep}key={Uri.EscapeDataString(LicenseKey)}";

        using var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "weixun-dotnet/0.1.0");
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WeixunException(EnvelopeCodes.FailGeneric, $"请求失败: {ex.Message}");
        }

        var status = (int)resp.StatusCode;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            throw new WeixunException(EnvelopeCodes.FailGeneric, text.Trim(), "", status, text);
        }

        using var doc = JsonDocument.Parse(text);
        var env = doc.RootElement.Deserialize<RawEnvelope>()
                  ?? throw new WeixunException(EnvelopeCodes.FailGeneric, "响应无法解析", "", status, text);

        if (!env.IsOk())
        {
            throw WeixunException.From(env, status, text);
        }
        return env.Data.Clone();
    }

    public void Dispose() => _http.Dispose();
}
