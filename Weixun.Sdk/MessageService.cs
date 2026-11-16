using System.Text.Json;

namespace Weixun;

/// <summary>单条发送的结果（已归一化协议端的字段差异）。</summary>
public sealed record MessageResult(bool Ok, string To, string ErrorMessage, JsonElement Raw);

/// <summary>
/// 消息服务：发送、群发、撤回，以及逐条结果归一化。
///
/// 协议端逐条结果字段不统一（实测）：
///  - 接收方：toUSerName（14 处）/ toUserName（9 处）
///  - 成功标志 7 种命名
///  - 错误信息：errMsg / errMgs（拼写错误）
/// </summary>
public sealed class MessageService
{
    public const int MsgTypeText = 1;
    public const int MsgTypeImage = 3;
    public const int MsgTypeVoice = 34;
    public const int MsgTypeCard = 42;
    public const int MsgTypeVideo = 43;
    public const int MsgTypeSticker = 47;
    public const int MsgTypeLocation = 48;
    public const int MsgTypeApp = 49;
    public const int MsgTypeSystem = 10000;
    public const int MsgTypeRevoke = 10002;

    private static readonly string[] SuccessFlags =
    {
        "isSendSuccess", "isCommentSuccess", "isMarkSuccess", "isHideSuccess",
        "isFollowSuccess", "isUploadSuccess", "isSuccess"
    };
    private static readonly string[] ToKeys = { "toUSerName", "toUserName", "to_user_name" };
    private static readonly string[] ErrorKeys = { "errMsg", "errMgs", "error" };

    private readonly WeixunClient _c;

    internal MessageService(WeixunClient c) => _c = c;

    /// <summary>发送一条文本消息。</summary>
    public async Task<MessageResult> SendTextAsync(
        string to, string text, IEnumerable<string>? at = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(to)) throw new ArgumentException("接收方不能为空", nameof(to));

        var item = new Dictionary<string, object>
        {
            ["ToUserName"] = to,
            ["TextContent"] = text,
            ["MsgType"] = 1
        };
        if (at is not null)
        {
            var atList = at.ToList();
            if (atList.Count > 0) item["AtWxIDList"] = atList;
        }

        var data = await _c.CallAsync(HttpMethod.Post, "/message/SendTextMessage",
            new Dictionary<string, object> { ["MsgItem"] = new[] { item } }, ct).ConfigureAwait(false);

        var results = ParseBatchResults(data);
        return results.Count > 0
            ? results[0]
            : throw new InvalidOperationException("协议端未返回发送结果");
    }

    /// <summary>发送图片（base64，可带 data URI 前缀）。</summary>
    public async Task<MessageResult> SendImageAsync(
        string to, string imageBase64, CancellationToken ct = default)
    {
        var data = await _c.CallAsync(HttpMethod.Post, "/message/SendImageMessage",
            new Dictionary<string, object>
            {
                ["MsgItem"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["ToUserName"] = to,
                        ["ImageContent"] = imageBase64,
                        ["MsgType"] = 2
                    }
                }
            }, ct).ConfigureAwait(false);

        var results = ParseBatchResults(data);
        return results.Count > 0
            ? results[0]
            : throw new InvalidOperationException("协议端未返回发送结果");
    }

    /// <summary>
    /// 向多个群/联系人批量发送相同文本。
    /// 注意：协议端返回 MassSendResponse 原样透传，没有收件人级别明细。
    /// </summary>
    public Task<JsonElement> GroupMassTextAsync(
        IEnumerable<string> to, string content, CancellationToken ct = default) =>
        _c.CallAsync(HttpMethod.Post, "/message/GroupMassMsgText",
            new Dictionary<string, object>
            {
                ["ToUserName"] = to.ToList(),
                ["Content"] = content
            }, ct);

    /// <summary>撤回消息（newMsgId 必须是字符串形式）。</summary>
    public Task RevokeAsync(string to, string newMsgId, string clientMsgId, CancellationToken ct = default) =>
        _c.CallAsync(HttpMethod.Post, "/message/RevokeMsg",
            new Dictionary<string, object>
            {
                ["ToUserName"] = to,
                ["NewMsgId"] = newMsgId,
                ["ClientMsgId"] = clientMsgId
            }, ct);

    /// <summary>标记会话已读。</summary>
    public Task MarkAllReadAsync(string to, CancellationToken ct = default) =>
        _c.CallAsync(HttpMethod.Post, "/message/MarkAllMessagesAsRead",
            new Dictionary<string, object> { ["ToUserName"] = to }, ct);

    /// <summary>设置 HTTP 消息回调（5 秒超时、不重试）。</summary>
    public Task SetCallbackAsync(string callbackUrl, CancellationToken ct = default) =>
        _c.CallAsync(HttpMethod.Post, "/message/SetCallback",
            new Dictionary<string, object> { ["CallbackUrl"] = callbackUrl }, ct);

    public static bool IsContactSync(int msgType) => msgType == MsgTypeSystem;
    public static bool IsRevoke(int msgType) => msgType == MsgTypeRevoke;

    /// <summary>把协议端五花八门的逐条结果统一成 MessageResult。</summary>
    public static List<MessageResult> ParseBatchResults(JsonElement data)
    {
        var outList = new List<MessageResult>();
        if (data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return outList;

        IEnumerable<JsonElement> items = data.ValueKind switch
        {
            JsonValueKind.Array => data.EnumerateArray(),
            JsonValueKind.Object => new[] { data },
            _ => Array.Empty<JsonElement>()
        };

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var to = "";
            foreach (var k in ToKeys)
            {
                if (item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) { to = s; break; }
                }
            }

            var err = "";
            foreach (var k in ErrorKeys)
            {
                if (item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) { err = s; break; }
                }
            }

            var hasFlag = false;
            var ok = false;
            foreach (var f in SuccessFlags)
            {
                if (item.TryGetProperty(f, out var v))
                {
                    hasFlag = true;
                    if (v.ValueKind == JsonValueKind.True) ok = true;
                }
            }
            if (!hasFlag) ok = true;
            if (err.Length > 0) ok = false;

            outList.Add(new MessageResult(ok, to, err, item.Clone()));
        }
        return outList;
    }
}
