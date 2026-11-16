using System.Text.Json;

namespace Weixun;

/// <summary>从流里解析出来的一条微信消息。</summary>
public sealed class StreamMessage
{
    public int MsgId { get; set; }
    /// <summary>64 位消息 ID，字符串形式（超过 2^53）</summary>
    public string NewMsgId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Type { get; set; }
    public string Content { get; set; } = "";
    public int CreateTime { get; set; }
    public string PushContent { get; set; } = "";

    public bool IsGroup => From.EndsWith("@chatroom");
}

/// <summary>type=record_content 事件（大聊天记录补拉完成后推送）。</summary>
public sealed class RecordContentEvent
{
    public string MessageId { get; set; } = "";
    public string Talker { get; set; } = "";
    public bool Success { get; set; }
    public string Content { get; set; } = "";
    public int Length { get; set; }
    public long Timestamp { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>
/// WebSocket 帧解析（纯函数，不负责建连）。
///
/// 协议端三种帧形态：
///  1. {"key","message","type":"message"} 普通消息
///  2. {"type":"record_content", ...} 记录补拉事件
///  3. "pong" / "sync completed" 裸文本控制帧
///
/// 建连可用 System.Net.WebSockets.ClientWebSocket，本类只做解析。
/// </summary>
public static class StreamFrame
{
    /// <summary>处理协议端两种字符串表达：{"str":"xxx"} 或裸字符串。</summary>
    public static string UnwrapString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Object when v.TryGetProperty("str", out var s) && s.ValueKind == JsonValueKind.String
            => s.GetString() ?? "",
        _ => ""
    };

    /// <summary>把整数（数字或字符串）统一转成字符串。</summary>
    public static string IntAsString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        _ => ""
    };

    /// <summary>解析 message 节点。</summary>
    public static StreamMessage? ParseAddMsg(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        return new StreamMessage
        {
            MsgId = node.TryGetProperty("msg_id", out var id) && id.TryGetInt32(out var i) ? i : 0,
            NewMsgId = node.TryGetProperty("new_msg_id", out var nid) ? IntAsString(nid) : "",
            From = node.TryGetProperty("from_user_name", out var f) ? UnwrapString(f) : "",
            To = node.TryGetProperty("to_user_name", out var t) ? UnwrapString(t) : "",
            Type = node.TryGetProperty("msg_type", out var mt) && mt.TryGetInt32(out var mti) ? mti : 0,
            Content = node.TryGetProperty("content", out var c) ? UnwrapString(c) : "",
            CreateTime = node.TryGetProperty("create_time", out var ct) && ct.TryGetInt32(out var cti) ? cti : 0,
            PushContent = node.TryGetProperty("push_content", out var pc) && pc.ValueKind == JsonValueKind.String
                ? pc.GetString() ?? "" : "",
        };
    }

    /// <summary>解析一帧原始文本，返回帧类型与内容。</summary>
    public static (string Kind, StreamMessage? Message, RecordContentEvent? Event) ParseFrame(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0 || text == "pong" || text == "sync completed" || text == "ping")
            return ("control", null, null);
        if (text[0] != '{') return ("unknown", null, null);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch { return ("unknown", null, null); }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tv) && tv.ValueKind == JsonValueKind.String
                ? tv.GetString() : "";

            if (type == "message")
            {
                if (!root.TryGetProperty("message", out var msgNode)) return ("unknown", null, null);
                var m = ParseAddMsg(msgNode);
                return m is null ? ("unknown", null, null) : ("message", m, null);
            }
            if (type == "record_content")
            {
                var e = new RecordContentEvent
                {
                    MessageId = root.TryGetProperty("message_id", out var mid) ? IntAsString(mid) : "",
                    Talker = root.TryGetProperty("talker", out var tk) && tk.ValueKind == JsonValueKind.String
                        ? tk.GetString() ?? "" : "",
                    Success = root.TryGetProperty("success", out var sc) && sc.ValueKind == JsonValueKind.True,
                    Content = root.TryGetProperty("content", out var cn) && cn.ValueKind == JsonValueKind.String
                        ? cn.GetString() ?? "" : "",
                    Length = root.TryGetProperty("length", out var ln) && ln.TryGetInt32(out var lni) ? lni : 0,
                    Timestamp = root.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var tsi) ? tsi : 0,
                    Error = root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String
                        ? er.GetString() ?? "" : "",
                };
                return ("record_content", null, e);
            }
            return ("unknown", null, null);
        }
    }

    /// <summary>构造 WebSocket 地址（http→ws / https→wss）。</summary>
    public static string WsUrl(string baseUrl, string licenseKey)
    {
        var b = baseUrl;
        if (b.StartsWith("https://")) b = "wss://" + b[8..];
        else if (b.StartsWith("http://")) b = "ws://" + b[7..];
        return b.TrimEnd('/') + "/ws/GetSyncMsg?key=" + Uri.EscapeDataString(licenseKey);
    }
}
