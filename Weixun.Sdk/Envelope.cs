using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weixun;

/// <summary>
/// 统一响应信封与错误类型。
///
/// 判定语义（协议端实测）：
///   1. Code 存在且为 0 或 200 → 成功
///   2. Code 不存在 → 回退 Success == true
///   3. 都无法判定 → 失败
///
/// 关键反例：/user/SetProxy 返回 Code=200 但 Success=false，绝不能单看 Success。
/// </summary>
public static class EnvelopeCodes
{
    public const int Success = 200;
    public const int LegacyOk = 0;
    public const int FailData = -1;
    public const int FailKey = -2;
    public const int FailBound = -3;
    public const int FailOffline = -4;
    public const int FailNotLocal = -5;
    public const int FailGeneric = 300;

    public const string ReasonLoginSessionNotFound = "LOGIN_SESSION_NOT_FOUND";
    public const string ReasonProxyOffline = "PROXY_OFFLINE";
    public const string ReasonAttemptConflict = "ATTEMPT_CONFLICT";
    public const string ReasonAttemptMismatch = "ATTEMPT_MISMATCH";
    public const string ReasonAttemptInactive = "ATTEMPT_INACTIVE";
    public const string ReasonAttemptIdRequired = "ATTEMPT_ID_REQUIRED";
    public const string ReasonAttemptNotFound = "ATTEMPT_NOT_FOUND";
    public const string ReasonAttemptExpired = "ATTEMPT_EXPIRED";
    public const string ReasonCancelled = "CANCELLED";
    public const string ReasonWakeScanRequired = "WAKE_SCAN_REQUIRED";
    public const string ReasonSecurityReloginRequired = "SECURITY_RELOGIN_REQUIRED";
    public const string ReasonSessionInvalid = "SESSION_INVALID";
    public const string ReasonWakeRequestFailed = "WAKE_REQUEST_FAILED";
    public const string ReasonLoginRequestFailed = "LOGIN_REQUEST_FAILED";
}

/// <summary>协议端统一响应结构。字段是 PascalCase，成功判定必须 Code 优先。</summary>
public sealed class RawEnvelope
{
    [JsonPropertyName("Code")]
    public JsonElement Code { get; set; }

    [JsonPropertyName("Data")]
    public JsonElement Data { get; set; }

    [JsonPropertyName("Text")]
    public string? Text { get; set; }

    [JsonPropertyName("Message")]
    public string? Message { get; set; }

    [JsonPropertyName("Success")]
    public bool? Success { get; set; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("attemptId")]
    public string? AttemptId { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    /// <summary>
    /// 把 Code 解析成整数。协议端的 Code 可能是 200（数字）或 "200"（字符串）。
    /// </summary>
    public int? ParseCode()
    {
        switch (Code.ValueKind)
        {
            case JsonValueKind.Number:
                return Code.TryGetInt32(out var n) ? n : null;
            case JsonValueKind.String:
                var s = (Code.GetString() ?? string.Empty).Trim();
                return int.TryParse(s, out var m) ? m : null;
            default:
                return null;
        }
    }

    /// <summary>按协议端真实语义判定成功。</summary>
    public bool IsOk()
    {
        var code = ParseCode();
        if (code.HasValue)
        {
            return code.Value == EnvelopeCodes.LegacyOk || code.Value == EnvelopeCodes.Success;
        }
        return Success == true;
    }

    /// <summary>可展示的失败说明。优先 Text，回退 Message（VerifyCode 用 Message）。</summary>
    public string MessageText()
    {
        var text = (Text ?? string.Empty).Trim();
        return text.Length > 0 ? text : (Message ?? string.Empty);
    }
}

/// <summary>所有业务失败的统一异常类型。</summary>
public sealed class WeixunException : Exception
{
    public int BizCode { get; }
    public string Text { get; }
    public string ReasonCode { get; }
    public int StatusCode { get; }
    public string Body { get; }

    public WeixunException(int bizCode, string text, string reasonCode = "", int statusCode = 200, string body = "")
        : base(reasonCode.Length == 0
            ? $"weixun: code={bizCode} msg={text}"
            : $"weixun: code={bizCode} reason={reasonCode} msg={text}")
    {
        BizCode = bizCode;
        Text = text;
        ReasonCode = reasonCode;
        StatusCode = statusCode;
        Body = body;
    }

    /// <summary>是否为 key 无效类错误，调用方据此提示重新登录。</summary>
    public bool IsKeyInvalid() =>
        BizCode == EnvelopeCodes.FailKey || ReasonCode == EnvelopeCodes.ReasonLoginSessionNotFound;

    /// <summary>是否疑似被风控限流。</summary>
    public bool IsRateLimited() =>
        StatusCode == 429 ||
        Text.Contains("频繁") || Text.Contains("限制") ||
        Text.Contains("frequency", StringComparison.OrdinalIgnoreCase);

    public static WeixunException From(RawEnvelope env, int statusCode, string body)
    {
        var code = env.ParseCode() ?? EnvelopeCodes.FailGeneric;
        var text = env.MessageText();
        if (text.Length == 0 && statusCode != 200) text = $"HTTP {statusCode}";
        return new WeixunException(code, text, env.ReasonCode ?? string.Empty, statusCode, body);
    }
}
