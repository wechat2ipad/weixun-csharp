using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Weixun;

namespace Weixun.Tests;

/// <summary>
/// Conformance 测试：读取契约仓同一批 fixtures，验证 .NET SDK 行为与其他语言一致。
/// </summary>
public class ConformanceTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string FixturesDir()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures"),
            Path.Combine(Directory.GetCurrentDirectory(), "conformance", "fixtures"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "weixun-api-contract", "fixtures"),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }
        throw new DirectoryNotFoundException("找不到 fixtures 目录");
    }

    private static JsonDocument Load(string rel) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir(), rel)));

    private static RawEnvelope ParseEnv(JsonElement input) =>
        input.Deserialize<RawEnvelope>(JsonOpts)!;

    // ── envelope ────────────────────────────────────────

    [Fact]
    public void Envelope_StandardOk()
    {
        using var fx = Load("envelope/01-ok.json");
        var input = fx.RootElement.GetProperty("input");
        var expect = fx.RootElement.GetProperty("expect");

        var env = ParseEnv(input);
        Assert.Equal(expect.GetProperty("ok").GetBoolean(), env.IsOk());
        Assert.Equal(expect.GetProperty("code").GetInt32(), env.ParseCode());
    }

    [Fact]
    public void Envelope_CodeIsString()
    {
        using var fx = Load("envelope/02-code-string.json");
        var env = ParseEnv(fx.RootElement.GetProperty("input"));
        Assert.True(env.IsOk());
        Assert.Equal(200, env.ParseCode());
    }

    /// <summary>关键反例：Code=200 但 Success 缺失/false，仍应判成功。</summary>
    [Fact]
    public void Envelope_SuccessFalseButCode200()
    {
        using var fx = Load("envelope/03-success-false-code-200.json");
        var env = ParseEnv(fx.RootElement.GetProperty("input"));
        Assert.True(env.IsOk());
        Assert.Equal(200, env.ParseCode());
    }

    [Fact]
    public void Envelope_VerifyCodeSpecialCase()
    {
        using var fx = Load("envelope/04-verify-code.json");
        var env = ParseEnv(fx.RootElement.GetProperty("input"));
        Assert.True(env.IsOk());
        Assert.Equal(0, env.ParseCode());
        Assert.Equal("验证成功", env.MessageText());
    }

    [Fact]
    public void Envelope_NegativeCode()
    {
        using var fx = Load("envelope/05-error-negative.json");
        var input = fx.RootElement.GetProperty("input");
        var expect = fx.RootElement.GetProperty("expect");
        var env = ParseEnv(input);

        Assert.False(env.IsOk());
        Assert.Equal(-2, env.ParseCode());
        Assert.Equal(expect.GetProperty("message_text").GetString()!.Trim(), env.MessageText());
    }

    [Fact]
    public void Envelope_ReasonCode()
    {
        using var fx = Load("envelope/06-reason-code.json");
        var env = ParseEnv(fx.RootElement.GetProperty("input"));
        Assert.False(env.IsOk());
        Assert.Equal("PROXY_OFFLINE", env.ReasonCode);
    }

    // ── message ─────────────────────────────────────────

    [Fact]
    public void Message_BatchPartialSuccess()
    {
        using var fx = Load("message/01-partial-success.json");
        var input = fx.RootElement.GetProperty("input");
        var expect = fx.RootElement.GetProperty("expect");

        var results = MessageService.ParseBatchResults(input);
        Assert.Equal(expect.GetProperty("count").GetInt32(), results.Count);

        var wantItems = expect.GetProperty("items").EnumerateArray().ToList();
        for (var i = 0; i < wantItems.Count; i++)
        {
            Assert.Equal(wantItems[i].GetProperty("to").GetString(), results[i].To);
            Assert.Equal(wantItems[i].GetProperty("ok").GetBoolean(), results[i].Ok);
            Assert.Equal(wantItems[i].GetProperty("error").GetString(), results[i].ErrorMessage);
        }
    }

    [Fact]
    public void Message_ErrMgsTypo()
    {
        using var fx = Load("message/02-errmgs-typo.json");
        var results = MessageService.ParseBatchResults(fx.RootElement.GetProperty("input"));
        Assert.Single(results);
        Assert.False(results[0].Ok);
        Assert.Equal("上传失败", results[0].ErrorMessage);
    }

    [Fact]
    public void Message_SingleObjectResult()
    {
        using var fx = Load("message/03-single-object.json");
        var results = MessageService.ParseBatchResults(fx.RootElement.GetProperty("input"));
        Assert.Single(results);
        Assert.Equal("wxid_a", results[0].To);
        Assert.True(results[0].Ok);
    }

    // ── 大整数 ──────────────────────────────────────────

    [Fact]
    public void BigInt_PreservedExactly()
    {
        const string raw = "{\"new_msg_id\":199344895681685661}";
        using var doc = JsonDocument.Parse(raw);
        var v = doc.RootElement.GetProperty("new_msg_id");
        Assert.Equal(JsonValueKind.Number, v.ValueKind);
        Assert.Equal(199344895681685661L, v.GetInt64());
    }

    // ── 限速 ────────────────────────────────────────────

    [Fact]
    public void RateLimiter_InitialBurstIsImmediate()
    {
        var lim = new RateLimiter(2, 100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lim.Acquire();
        lim.Acquire();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 20, $"前两次应立即可用，实际 {sw.ElapsedMilliseconds}ms");
    }

    // ── 元信息 ──────────────────────────────────────────

    [Fact]
    public void Client_KeyMasking()
    {
        using var c = new WeixunClient("http://127.0.0.1:7006", "abcdefgh12345678");
        Assert.Equal("abcd****5678", c.KeyMasked);
    }

    [Fact]
    public void Exception_KeyInvalidMapping()
    {
        var env = ParseEnv(JsonDocument.Parse("{\"Code\":-2,\"Text\":\"该链接不存在\",\"Success\":false}").RootElement);
        var err = WeixunException.From(env, 200, "");
        Assert.Equal(-2, err.BizCode);
        Assert.True(err.IsKeyInvalid());
    }

    // ── ws 帧解析 ───────────────────────────────────────

    [Fact]
    public void Ws_MessageFrame()
    {
        const string raw = @"{
          ""key"": ""a1b2c3"",
          ""message"": {
            ""msg_id"": 1751755490,
            ""from_user_name"": {""str"": ""50190590530@chatroom""},
            ""to_user_name"": {""str"": ""wxid_hvoabx1dxaci22""},
            ""msg_type"": 1,
            ""content"": {""str"": ""hello""},
            ""create_time"": 1789611963,
            ""new_msg_id"": 199344895681685661
          },
          ""type"": ""message""
        }";

        var (kind, msg, _) = StreamFrame.ParseFrame(raw);
        Assert.Equal("message", kind);
        Assert.NotNull(msg);
        Assert.Equal("50190590530@chatroom", msg!.From);
        Assert.Equal("wxid_hvoabx1dxaci22", msg.To);
        Assert.Equal(1, msg.Type);
        Assert.True(msg.IsGroup);
        Assert.Equal("199344895681685661", msg.NewMsgId);
        Assert.Equal(1751755490, msg.MsgId);
    }

    [Fact]
    public void Ws_RecordContentEvent()
    {
        const string raw = @"{
          ""key"": ""k1"", ""type"": ""record_content"", ""message_id"": ""123456789"",
          ""talker"": ""wxid_a"", ""success"": true, ""content"": ""<msg/>"",
          ""length"": 120, ""timestamp"": 1789611963
        }";
        var (kind, _, ev) = StreamFrame.ParseFrame(raw);
        Assert.Equal("record_content", kind);
        Assert.NotNull(ev);
        Assert.Equal("123456789", ev!.MessageId);
        Assert.Equal("wxid_a", ev.Talker);
        Assert.True(ev.Success);
    }

    [Fact]
    public void Ws_PlainTextControlFrames()
    {
        foreach (var t in new[] { "pong", "sync completed", "ping" })
        {
            var (kind, msg, ev) = StreamFrame.ParseFrame(t);
            Assert.Equal("control", kind);
            Assert.Null(msg);
            Assert.Null(ev);
        }
    }

    [Fact]
    public void Ws_UrlConstruction()
    {
        Assert.Equal("ws://127.0.0.1:7006/ws/GetSyncMsg?key=mykey",
            StreamFrame.WsUrl("http://127.0.0.1:7006", "mykey"));
        Assert.StartsWith("wss://", StreamFrame.WsUrl("https://example.com", "k"));
    }

    [Fact]
    public void Ws_UnwrapAndIntAsString()
    {
        using var doc = JsonDocument.Parse("{\"str\":\"abc\"}");
        Assert.Equal("abc", StreamFrame.UnwrapString(doc.RootElement));

        using var doc2 = JsonDocument.Parse("\"abc\"");
        Assert.Equal("abc", StreamFrame.UnwrapString(doc2.RootElement));

        using var doc3 = JsonDocument.Parse("199344895681685661");
        Assert.Equal("199344895681685661", StreamFrame.IntAsString(doc3.RootElement));
    }

    // ── login 状态机 ────────────────────────────────────

    [Fact]
    public void Login_StateMachine()
    {
        using var fx = Load("login/01-state-machine.json");
        foreach (var c in fx.RootElement.GetProperty("cases").EnumerateArray())
        {
            var stateName = c.GetProperty("loginState").GetString()!;
            var st = LoginState.Parse(JsonDocument.Parse(
                $"{{\"loginState\":\"{stateName}\"}}").RootElement);
            Assert.Equal(c.GetProperty("online").GetBoolean(), st.IsOnline);
            Assert.Equal(c.GetProperty("terminal").GetBoolean(), st.IsTerminal);
        }
    }

    // ── upload ──────────────────────────────────────────

    [Fact]
    public void Upload_StripDataUri()
    {
        Assert.Equal("abc", UploadService.StripDataUri("data:image/png;base64,abc"));
        Assert.Equal("abc", UploadService.StripDataUri("abc"));
    }
}
