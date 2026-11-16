namespace Weixun;

/// <summary>
/// 上传：base64 辅助与 JSON 上传接口。
///
/// 协议端唯一的 multipart 接口是 /message/CdnBigFileUpload；
/// 其余"上传"全部是 JSON + base64。
/// </summary>
public sealed class UploadService
{
    // 协议端 fileType 取值
    public const int FileTypeImage = 2;
    public const int FileTypeVideo = 4;
    public const int FileTypeFile = 5;

    private readonly WeixunClient _c;

    internal UploadService(WeixunClient c) => _c = c;

    /// <summary>读取本地文件并编码为 base64。</summary>
    public static string Base64FromFile(string path) =>
        Convert.ToBase64String(File.ReadAllBytes(path));

    /// <summary>生成协议端可直接接受的 data URI。</summary>
    public static string ImageDataUri(string mime, byte[] bytes) =>
        $"data:{(string.IsNullOrEmpty(mime) ? "image/png" : mime)};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>去掉 data URI 前缀，只留纯 base64。</summary>
    public static string StripDataUri(string v)
    {
        if (v.StartsWith("data:"))
        {
            var idx = v.IndexOf(',');
            if (idx >= 0) return v[(idx + 1)..];
        }
        return v;
    }

    /// <summary>上传图片到微信 CDN。</summary>
    public Task<System.Text.Json.JsonElement> UploadImageToCdnAsync(
        string imageBase64, CancellationToken ct = default) =>
        _c.CallAsync(HttpMethod.Post, "/message/UploadImageToCDN",
            new Dictionary<string, object> { ["imageContent"] = imageBase64 }, ct);

    /// <summary>上传头像。</summary>
    public Task<System.Text.Json.JsonElement> UploadHeadImageAsync(
        string imageBase64, CancellationToken ct = default) =>
        _c.CallAsync(HttpMethod.Post, "/user/UploadHeadImage",
            new Dictionary<string, object> { ["Base64"] = imageBase64 }, ct);

    /// <summary>发送语音（base64）。</summary>
    public Task<System.Text.Json.JsonElement> SendVoiceAsync(
        string to, string voiceBase64, int second, CancellationToken ct = default) =>
        _c.CallAsync(HttpMethod.Post, "/message/SendVoice",
            new Dictionary<string, object>
            {
                ["ToUserName"] = to,
                ["VoiceData"] = voiceBase64,
                ["VoiceSecond"] = second
            }, ct);
}
