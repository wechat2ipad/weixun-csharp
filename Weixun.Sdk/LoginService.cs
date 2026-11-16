using System.Text.Json;

namespace Weixun;

/// <summary>
/// 扫码登录状态机。
///
/// 状态机（协议端实测 loginState 取值）：
///  - waiting 等待扫码
///  - login_processing 已扫码，处理中
///  - ipad_required 需要 iPad 二次确认（双扫）
///  - online 登录完成
///  - security_verify_required 需要安全验证，必须重新登录
/// </summary>
public sealed class LoginState
{
    public int State { get; set; }
    public string LoginStateValue { get; set; } = "";
    public string NextStep { get; set; } = "";
    public string QrCodeUrl { get; set; } = "";
    public string WxId { get; set; } = "";
    public string Message { get; set; } = "";
    public string AttemptId { get; set; } = "";

    public bool IsOnline => LoginStateValue == "online";
    public bool NeedIpadConfirm => LoginStateValue == "ipad_required";
    public bool NeedSecurityVerify => LoginStateValue == "security_verify_required";
    public bool IsTerminal => IsOnline || NeedSecurityVerify;

    public static LoginState Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return new LoginState();
        string S(string k) => data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
        var qr = S("qrCodeUrl");
        return new LoginState
        {
            State = data.TryGetProperty("state", out var st) && st.TryGetInt32(out var si) ? si : 0,
            LoginStateValue = S("loginState"),
            NextStep = S("nextStep"),
            QrCodeUrl = qr.Length > 0 ? qr : S("QrCodeUrl"),
            WxId = S("wxid"),
            Message = S("msg"),
            AttemptId = S("attemptId"),
        };
    }
}

/// <summary>登录服务。</summary>
public sealed class LoginService
{
    private readonly WeixunClient _c;

    internal LoginService(WeixunClient c) => _c = c;

    /// <summary>
    /// 获取登录二维码。协议端要求同一 key 同时只有一个活跃尝试（15 分钟 TTL）。
    /// </summary>
    public async Task<LoginState> GetQrCodeAsync(string device = "ipad", CancellationToken ct = default)
    {
        var data = await _c.CallAsync(HttpMethod.Post, "/login/GetLoginQrCodeNewDirect",
            new Dictionary<string, object> { ["device"] = string.IsNullOrEmpty(device) ? "ipad" : device }, ct)
            .ConfigureAwait(false);
        return LoginState.Parse(data);
    }

    /// <summary>查询登录进度。建议每 2.5 秒一次。</summary>
    public async Task<LoginState> CheckStatusAsync(string attemptId = "", CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(attemptId)) body["attemptId"] = attemptId;
        var data = await _c.CallAsync(HttpMethod.Post, "/login/CheckLoginStatus", body, ct)
            .ConfigureAwait(false);
        var st = LoginState.Parse(data);
        if (st.AttemptId.Length == 0) st.AttemptId = attemptId;
        return st;
    }

    /// <summary>阻塞等待登录完成。</summary>
    public async Task<LoginState> WaitForOnlineAsync(
        string attemptId = "", int intervalMs = 2500, int timeoutMs = 300_000,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var st = await CheckStatusAsync(attemptId, ct).ConfigureAwait(false);
            if (st.IsOnline) return st;
            if (st.NeedSecurityVerify)
            {
                throw new WeixunException(EnvelopeCodes.FailGeneric,
                    "需要安全验证，必须重新发起登录",
                    EnvelopeCodes.ReasonSecurityReloginRequired);
            }
            await Task.Delay(intervalMs, ct).ConfigureAwait(false);
        }
        throw new WeixunException(EnvelopeCodes.FailGeneric, "等待登录超时");
    }

    /// <summary>取消当前登录尝试。</summary>
    public Task CancelAsync(string attemptId = "", CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(attemptId)) body["attemptId"] = attemptId;
        return _c.CallAsync(HttpMethod.Post, "/login/CancelLogin", body, ct);
    }

    /// <summary>查询账号当前是否在线。</summary>
    public Task<JsonElement> GetLoginStatusAsync(CancellationToken ct = default) =>
        _c.CallAsync(HttpMethod.Get, "/login/GetLoginStatus", null, ct);

    /// <summary>唤醒已登录过的账号（避免重新扫码）。</summary>
    public async Task<LoginState> WakeUpAsync(string attemptId = "", CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(attemptId)) body["attemptId"] = attemptId;
        var data = await _c.CallAsync(HttpMethod.Post, "/login/WakeUpLogin", body, ct)
            .ConfigureAwait(false);
        return LoginState.Parse(data);
    }
}
