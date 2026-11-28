// Ports the assertions of app/check_providers.swift (all but checkKeychainStore: the Windows Credential Manager store is tested in Murmur.Platform).
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>Records requests; returns a canned result or throws; optionally delays (honoring cancellation).</summary>
internal sealed class MockTransport(Func<HttpResult> response, double delaySeconds = 0) : IHttpTransport
{
    readonly Lock gate = new();
    readonly List<ProviderRequest> requests = [];
    public MockTransport(HttpResult result, double delaySeconds = 0) : this(() => result, delaySeconds) { }
    public static MockTransport Failing(TransportError code) => new(() => throw new TransportException(code));
    public IReadOnlyList<ProviderRequest> Requests { get { lock (gate) return [.. requests]; } }
    public async Task<HttpResult> SendAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        lock (gate) requests.Add(request);
        if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        return response();
    }
}

/// <summary>Returns a canned result per URL path (404 otherwise); records requests.</summary>
internal sealed class RoutingTransport(Dictionary<string, HttpResult> routes) : IHttpTransport
{
    readonly Lock gate = new();
    readonly List<ProviderRequest> requests = [];
    public IReadOnlyList<ProviderRequest> Requests { get { lock (gate) return [.. requests]; } }
    public Task<HttpResult> SendAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        lock (gate) requests.Add(request);
        return Task.FromResult(routes.GetValueOrDefault(request.Url.Path) ?? new HttpResult(404, null, Encoding.UTF8.GetBytes("not found")));
    }
}

public class ProvidersTests
{
    // Fake key shaped like a real one; never valid anywhere.
    const string FakeSecret = "sk-or-v1-FAKEKEY0123456789abcdefFAKE";
    static readonly APIKey Key = APIKey.Create(FakeSecret)!;
    const string TranscriptSecret = "Synthetic private dictation about Project Nightingale";

    internal static HttpResult Result(int status, string body, Dictionary<string, string>? headers = null) => new(status, headers, Encoding.UTF8.GetBytes(body));
    static JsonElement Json(byte[]? body) => JsonDocument.Parse(body ?? "{}"u8.ToArray()).RootElement;

    internal static void Expect(ProviderError expected, Action body)
    {
        var e = Assert.Throws<ProviderException>(body);
        Assert.Equal(expected, e.Error);
    }

    internal static async Task ExpectAsync(ProviderError expected, Func<Task> body)
    {
        var e = await Assert.ThrowsAsync<ProviderException>(body);
        Assert.Equal(expected, e.Error);
    }

    const string SttSuccess = """{"text": "Hello, this is a test of speech-to-text transcription.", "usage": {"seconds": 9.2, "total_tokens": 113, "input_tokens": 83, "output_tokens": 30, "cost": 0.000508}}""";
    const string ChatSuccess = """{"id": "gen-123", "provider": "Google", "model": "google/gemini-3.5-flash-lite", "object": "chat.completion", "created": 1791000000, "choices": [{"logprobs": null, "finish_reason": "stop", "native_finish_reason": "STOP", "index": 0, "message": {"role": "assistant", "content": "Shopping list:\n- apples\n- milk\n- bread", "refusal": null}}], "usage": {"prompt_tokens": 40, "completion_tokens": 12, "total_tokens": 52}}""";
    const string ChatCredits = """{"error": {"code": 402, "message": "Insufficient credits. Add more using https://openrouter.ai/credits"}}""";
    const string UnauthorizedBody = """{"error":{"message":"Invalid API key.","code":"UNAUTHORIZED","type":"error"}}""";
    const string ChatRate = """{"error": {"code": 429, "message": "Rate limit exceeded"}}""";
    const string ChatMidStream = """{"id": "gen-9", "error": {"code": 502, "message": "Provider returned error"}}""";
    const string ChatUnavailable = """{"error": {"code": 503, "message": "No endpoints found matching your data policy"}}""";
    const string ChatLength = """{"choices": [{"finish_reason": "length", "message": {"role": "assistant", "content": "Shopping"}}]}""";

    const string LocalChatOK = """{"id":"chatcmpl-1","object":"chat.completion","created":1791000000,"model":"llama3.2:latest","system_fingerprint":"fp_ollama","choices":[{"index":0,"message":{"role":"assistant","content":"Please send the report."},"finish_reason":"stop"}],"usage":{"prompt_tokens":60,"completion_tokens":6,"total_tokens":66}}""";
    const string LocalChatLength = """{"choices":[{"index":0,"message":{"role":"assistant","content":"Please"},"finish_reason":"length"}]}""";
    const string OllamaModelNotFound = """{"error":{"message":"model \"llama9\" not found, try pulling it first","type":"api_error","param":null,"code":null}}""";
    const string OllamaV1Models = """{"object":"list","data":[{"id":"qwen2.5:7b","object":"model","created":1790000000,"owned_by":"library"},{"id":"llama3.2:latest","object":"model","created":1790000001,"owned_by":"library"}]}""";
    const string LlamaServerModels = """{"models":[{"name":"gemma-3-4b-it-Q4_K_M.gguf","model":"gemma-3-4b-it-Q4_K_M.gguf","modified_at":"","size":"","digest":"","type":"model","capabilities":["completion"]}],"object":"list","data":[{"id":"gemma-3-4b-it-Q4_K_M.gguf","object":"model","created":1790000000,"owned_by":"llamacpp","meta":{"n_params":3880000000}}]}""";
    const string OllamaTags = """{"models":[{"name":"llama3.2:latest","model":"llama3.2:latest","modified_at":"2026-09-01T10:00:00.000000-07:00","size":2019393189,"digest":"a80c4f17","details":{"format":"gguf","family":"llama","parameter_size":"3.2B","quantization_level":"Q4_K_M"}},{"name":"mistral:7b","model":"mistral:7b","modified_at":"2026-08-01T10:00:00Z","size":4100000000,"digest":"f974a74358d6","details":{"format":"gguf","family":"llama","parameter_size":"7B","quantization_level":"Q4_0"}}]}""";

    /// <summary>Minimal multipart parser for checks: name → content bytes.</summary>
    internal static Dictionary<string, byte[]> SplitMultipart(byte[] body, string boundary)
    {
        var delimiter = Encoding.UTF8.GetBytes("--" + boundary);
        var starts = new List<int>();
        for (var i = 0; i + delimiter.Length <= body.Length; i++) if (body.AsSpan(i, delimiter.Length).SequenceEqual(delimiter)) starts.Add(i);
        var parts = new Dictionary<string, byte[]>();
        for (var k = 0; k + 1 < starts.Count; k++)
        {
            var chunk = body[(starts[k] + delimiter.Length)..starts[k + 1]];
            if (chunk.Length < 2 || chunk[0] != '\r' || chunk[1] != '\n') continue;
            chunk = chunk[2..];
            var headerEnd = chunk.AsSpan().IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0) continue;
            var header = Encoding.UTF8.GetString(chunk, 0, headerEnd);
            var content = chunk[(headerEnd + 4)..];
            if (content.Length >= 2 && content[^2] == '\r' && content[^1] == '\n') content = content[..^2];
            var name = header.IndexOf("name=\"", StringComparison.Ordinal);
            if (name < 0) continue;
            var rest = header[(name + 6)..];
            var end = rest.IndexOf('"');
            if (end < 0) continue;
            parts[rest[..end]] = content;
        }
        return parts;
    }

    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    // MARK: Keys and credentials

    [Fact]
    public void APIKeyAndCredentials()
    {
        Assert.True(APIKey.Create("") is null && APIKey.Create("   \n") is null, "empty key rejected");
        Assert.True(APIKey.Create("ab cd") is null && APIKey.Create("ab\ncd") is null, "embedded whitespace rejected");
        Assert.Equal("abc123", APIKey.Create("  abc123 \n")?.Secret);
        Assert.Null(APIKey.Create("ключ"));
        // Swift's description, reflection and dump: here ToString, interpolation, the debugger display and JSON serialization.
        Assert.DoesNotContain(FakeSecret, $"{Key}");
        Assert.DoesNotContain(FakeSecret, JsonSerializer.Serialize(Key));
        Assert.DoesNotContain(FakeSecret, ((DebuggerDisplayAttribute)typeof(APIKey).GetCustomAttributes(typeof(DebuggerDisplayAttribute), false)[0]).Value);
        var provider = new OpenAICompatibleCleanupProvider("https://api.example.com/v1", "m", Key, ProviderID.CustomAPI);
        Assert.DoesNotContain(FakeSecret, provider.ToString());
        Assert.DoesNotContain(FakeSecret, JsonSerializer.Serialize(provider));

        var store = new InMemoryCredentialStore();
        Assert.Null(store.Read(ProviderID.CustomTranscription));
        store.Save(APIKey.Create("first")!, ProviderID.CustomTranscription);
        store.Save(APIKey.Create("second")!, ProviderID.CustomTranscription);
        store.Save(APIKey.Create("other")!, ProviderID.CustomCleanup);
        Assert.Equal("second", store.Read(ProviderID.CustomTranscription)?.Secret);
        store.Delete(ProviderID.CustomTranscription);
        Assert.Null(store.Read(ProviderID.CustomTranscription));
        store.Delete(ProviderID.CustomTranscription); // deleting a missing key succeeds
        store.DeleteAll();
        Assert.Null(store.Read(ProviderID.CustomCleanup));
        Assert.Equal(["openrouter", "cerebras", "customAPI", "customTranscription", "customCleanup", "localServer", "appleIntelligence", "local-model"],
                     Enum.GetValues<ProviderID>().Select(p => p.RawValue()));
        Assert.Equal("Keychain operation failed (status -25300).", CredentialError.Keychain(-25300).Description);
    }

    // MARK: Multipart

    [Fact]
    public void MultipartShape()
    {
        byte[] audioBytes = [0x52, 0x49, 0x46, 0x46, 0x00, 0xFF, 0x0D, 0x0A, 0x2D, 0x2D];
        var audio = new AudioPayload(audioBytes, "clip.wav", "audio/wav", "en");
        const string boundary = "TESTBOUNDARY";
        var fw = OpenAITranscriptionFormat.MakeRequest(ProviderUrl.Parse("https://stt.example.test/v1/audio/transcriptions"), Key, "whisper-1", audio, boundary);
        Assert.Equal("POST", fw.Method);
        Assert.Equal("https://stt.example.test/v1/audio/transcriptions", fw.Url.ToString());
        Assert.Equal($"Bearer {FakeSecret}", fw.Header("Authorization"));
        Assert.Equal("multipart/form-data; boundary=TESTBOUNDARY", fw.Header("Content-Type"));
        Assert.DoesNotContain(FakeSecret, fw.Url.ToString());
        var body = fw.Body!;
        var parts = SplitMultipart(body, boundary);
        Assert.Equal(B("whisper-1"), parts["model"]);
        Assert.Equal(B("json"), parts["response_format"]);
        byte[]? Format(string endpoint, string model) =>
            SplitMultipart(OpenAITranscriptionFormat.MakeRequest(ProviderUrl.Parse(endpoint), Key, model, audio, boundary).Body!, boundary).GetValueOrDefault("response_format");
        Assert.Equal(B("verbose_json"), Format("https://api.groq.com/openai/v1/audio/transcriptions", "whisper-large-v3-turbo"));
        Assert.Equal(B("verbose_json"), Format("https://api.openai.com/v1/audio/transcriptions", "whisper-1"));
        Assert.Equal(B("json"), Format("https://api.openai.com/v1/audio/transcriptions", "gpt-4o-transcribe"));
        Assert.Equal(B("json"), Format("https://openrouter.ai/api/v1/audio/transcriptions", "openai/whisper-large-v3-turbo"));
        Assert.Equal(B("en"), parts["language"]);
        Assert.False(parts.ContainsKey("prompt"), "custom transcription omits free-form dictionary prompts");
        Assert.Equal(audioBytes, parts["file"]);
        Assert.Contains("Content-Disposition: form-data; name=\"file\"; filename=\"clip.wav\"\r\nContent-Type: audio/wav\r\n\r\n", Encoding.UTF8.GetString(body));
        Assert.Equal(B("\r\n--TESTBOUNDARY--\r\n"), body[^20..]);

        var hostile = new AudioPayload(audioBytes, "a\"\r\nX-Evil: 1.wav", "audio/wav\r\nX: y", null);
        var hostileBody = Encoding.UTF8.GetString(OpenAITranscriptionFormat.MakeRequest(ProviderUrl.Parse("https://stt.example.test/v1/audio/transcriptions"), Key, "whisper-1", hostile, boundary).Body!);
        Assert.True(!hostileBody.Contains("\r\nX-Evil") && !hostileBody.Contains("\r\nX: y"), "multipart header injection stripped");
        Assert.False(SplitMultipart(B(hostileBody), boundary).ContainsKey("language"), "nil language omitted");
    }

    // MARK: Parsing

    static string Segmented((string Text, double NoSpeech, double Logprob)[] segments)
    {
        var parts = segments.Select(s => $$"""{"text": "{{s.Text}}", "no_speech_prob": {{s.NoSpeech}}, "avg_logprob": {{s.Logprob}}, "start": 0, "end": 1, "compression_ratio": 1.2}""");
        return OpenAITranscriptionFormat.Parse(ProviderID.CustomAPI, Result(200, $$"""{"text": "{{string.Concat(segments.Select(s => s.Text))}}", "segments": [{{string.Join(",", parts)}}]}"""));
    }

    static string Timed((string Text, double Start, double End)[] segments, float[] levels)
    {
        var parts = segments.Select(s => $$"""{"text": "{{s.Text}}", "no_speech_prob": 0.1, "avg_logprob": -0.3, "start": {{s.Start}}, "end": {{s.End}}}""");
        return OpenAITranscriptionFormat.Parse(ProviderID.CustomAPI, Result(200, $$"""{"text": "{{string.Concat(segments.Select(s => s.Text))}}", "segments": [{{string.Join(",", parts)}}]}"""), levels);
    }

    [Fact]
    public void Parsing()
    {
        var c = ProviderID.CustomAPI;
        Assert.Equal("Hello, this is a test of speech-to-text transcription.", OpenAITranscriptionFormat.Parse(c, Result(200, SttSuccess)));
        Assert.Equal("", OpenAITranscriptionFormat.Parse(c, Result(200, """{"text": ""}""")));
        Expect(ProviderError.MalformedResponse(c), () => OpenAITranscriptionFormat.Parse(c, Result(200, "<html>")));
        Expect(ProviderError.Authentication(c), () => OpenAITranscriptionFormat.Parse(c, Result(401, UnauthorizedBody)));
        Expect(ProviderError.Server(c, 503), () => OpenAITranscriptionFormat.Parse(c, Result(503, "no healthy upstream")));
        Assert.Equal("Send the report.", Segmented([(" Send the report.", 0.01, -0.2), (" Maybe shower.", 0.9, -1.5)]));
        Assert.Equal(" Send the report. Thank you.", Segmented([(" Send the report.", 0.01, -0.2), (" Thank you.", 0.1, -0.3)]));
        Assert.Equal(" One. um Two.", Segmented([(" One.", 0.01, -0.2), (" um", 0.9, -1.5), (" Two.", 0.02, -0.2)]));
        Assert.Equal("One. um Two.", Segmented([(" One.", 0.01, -0.2), (" um", 0.9, -1.5), (" Two.", 0.02, -0.2), (" Thank you.", 0.95, -1.2)]));
        Assert.Equal("", Segmented([(" Thank you.", 0.9, -1.5), (" Bye.", 0.8, -1.1)]));
        Assert.Equal(" Send it. Thank you.", Segmented([(" Send it.", 0.01, -0.2), (" Thank you.", 0.9, -0.5)]));
        Assert.Equal("", Segmented([(" I'm going to go ahead and get started.", 0, -1.4)]));
        Assert.Equal(" Ship it on Monday.", Segmented([(" Ship it on Monday.", 0, -0.8)]));
        Assert.Equal(" Ship it on Monday. And the docs.", Segmented([(" Ship it on Monday.", 0, -0.3), (" And the docs.", 0, -1.4)]));
        float[] spokeThenQuiet = [.. Enumerable.Repeat(0.3f, 10), .. Enumerable.Repeat(0.002f, 10)];
        Assert.Equal("Send the report.", Timed([(" Send the report.", 0, 1), (" Thank you.", 1.2, 1.9)], spokeThenQuiet));
        Assert.Equal(" Send the report. Thank you.", Timed([(" Send the report.", 0, 1), (" Thank you.", 1.2, 1.9)], [.. Enumerable.Repeat(0.3f, 20)]));
        Assert.Equal("Send the report.", Timed([(" Send the report.", 0, 1), (" Thank you.", 2.5, 3)], spokeThenQuiet));
        var clicked = spokeThenQuiet.ToArray(); clicked[15] = 0.6f;
        Assert.Equal("Send the report.", Timed([(" Send the report.", 0, 1), (" Thank you.", 1.2, 1.9)], clicked));
        Assert.Equal(" One. Two. Three.", Timed([(" One.", 0, 0.5), (" Two.", 1.2, 1.4), (" Three.", 0.5, 1)], spokeThenQuiet));
        Assert.True(OpenAITranscriptionFormat.LoudWindows([0.3f, 0.3f, 0.002f], 0, 0.25) == 2 && OpenAITranscriptionFormat.LoudWindows([0.3f], 4, 5) == 0);
        var detailed = OpenAITranscriptionFormat.ParseDetailed(c, Result(200, """{"text": " Send the report. Thank you.", "segments": [{"text": " Send the report.", "no_speech_prob": 0.01, "avg_logprob": -0.2, "compression_ratio": 1.1, "start": 0, "end": 1}, {"text": " Thank you.", "no_speech_prob": 0.1, "avg_logprob": -0.3, "start": 1.2, "end": 1.9}]}"""), spokeThenQuiet);
        Assert.True(detailed.Text == "Send the report." && detailed.Segments!.Select(s => s.Dropped).SequenceEqual([false, true]) && detailed.Segments![0].LoudWindows == 10 && detailed.Segments[1].LoudWindows == 0
                    && detailed.Segments[0].AvgLogprob == -0.2 && detailed.Segments[0].CompressionRatio == 1.1 && detailed.Segments[1].CompressionRatio is null, "segment detail keeps scores, loud windows and the dropped flag");
        Assert.Null(OpenAITranscriptionFormat.ParseDetailed(c, Result(200, SttSuccess)).Segments);
        const string partial = """{"text": " Send it. Thank you.", "segments": [{"text": " Send it.", "start": 0, "end": 1}, {"text": " Thank you.", "no_speech_prob": 0.9, "avg_logprob": -1.5}]}""";
        Assert.Equal(" Send it. Thank you.", OpenAITranscriptionFormat.Parse(c, Result(200, partial)));
        Assert.Equal(" Hi.", OpenAITranscriptionFormat.Parse(c, Result(200, """{"text": " Hi.", "segments": "x"}""")));

        Assert.Equal("Shopping list:\n- apples\n- milk\n- bread", OpenAIChatFormat.Parse(c, Result(200, ChatSuccess)));
        Expect(ProviderError.Server(c, 502), () => OpenAIChatFormat.Parse(c, Result(200, ChatMidStream)));
        Expect(ProviderError.OutputTruncated(c), () => OpenAIChatFormat.Parse(c, Result(200, ChatLength)));
        Expect(ProviderError.MalformedResponse(c), () => OpenAIChatFormat.Parse(c, Result(200, """{"choices": []}""")));
    }

    // MARK: Classification

    [Fact]
    public void Classification()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_791_000_000);
        ProviderError C(int status, string body, Dictionary<string, string>? headers = null) => ProviderErrorClassifier.Classify(ProviderID.CustomAPI, Result(status, body, headers), now);
        var c = ProviderID.CustomAPI;
        Assert.Equal(ProviderError.Authentication(c), C(403, "{}"));
        Assert.True(C(400, "{}") == ProviderError.Rejected(c, 400) && C(404, "{}") == ProviderError.Rejected(c, 404));
        Assert.Equal(ProviderError.Timeout(c), C(504, ""));
        Assert.Equal(ProviderError.Authentication(c), C(401, UnauthorizedBody));
        Assert.Equal(ProviderError.Quota(c), C(402, ChatCredits));
        Assert.Equal(ProviderError.RateLimited(c, 60), C(429, ChatRate, new() { ["Retry-After"] = "60" }));
        Assert.Equal(ProviderError.RateLimited(c, 30), C(429, ChatRate, new() { ["retry-after"] = "Sat, 03 Oct 2026 04:00:30 GMT" }));
        Assert.Equal(ProviderError.RateLimited(c, null), C(429, ChatRate));
        Assert.Equal(ProviderError.Server(c, 503), C(503, ChatUnavailable));
        Assert.Equal(ProviderError.Timeout(c), C(408, ""));
        Assert.Equal(ProviderError.PayloadTooLarge(c, null), C(413, "{}"));
        Assert.Equal(ProviderError.Server(c, 520), C(520, ""));
        Assert.Equal(ProviderError.Server(c, null), C(200, "not json"));

        Assert.Equal(ProviderError.Network(c), ProviderErrorClassifier.Classify(c, new TransportException(TransportError.NotConnectedToInternet)));
        Assert.Equal(ProviderError.Network(c), ProviderErrorClassifier.Classify(c, new TransportException(TransportError.CannotFindHost)));
        Assert.Equal(ProviderError.Timeout(c), ProviderErrorClassifier.Classify(c, new TransportException(TransportError.TimedOut)));
        Assert.Equal(ProviderError.Cancelled, ProviderErrorClassifier.Classify(c, new TransportException(TransportError.Cancelled)));
        Assert.Equal(ProviderError.Cancelled, ProviderErrorClassifier.Classify(c, new OperationCanceledException()));

        Assert.Equal(SessionDisposition.PreserveRetryLater, ProviderError.RateLimited(c, 3).Disposition);
        Assert.True(ProviderError.Network(c).Disposition == SessionDisposition.PreserveRetryLater && ProviderError.Timeout(c).Disposition == SessionDisposition.PreserveRetryLater);
        Assert.True(ProviderError.Server(c, 500).Disposition == SessionDisposition.PreserveRetryLater && ProviderError.MalformedResponse(c).Disposition == SessionDisposition.PreserveRetryLater);
        Assert.True(ProviderError.Authentication(c).Disposition == SessionDisposition.PreserveNeedsChange && ProviderError.Quota(c).Disposition == SessionDisposition.PreserveNeedsChange);
        Assert.Equal(SessionDisposition.PreserveNeedsChange, ProviderError.PayloadTooLarge(c, 1).Disposition);
        Assert.Equal(SessionDisposition.Discard, ProviderError.Cancelled.Disposition);
        Assert.Contains("2 seconds", ProviderError.RateLimited(c, 1.2).UserMessage);
        foreach (var value in new[] { double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.MaxValue, (double)long.MaxValue })
            Assert.DoesNotContain("seconds", ProviderError.RateLimited(c, value).UserMessage);
        foreach (var value in new[] { "inf", "nan", "1e100", long.MaxValue.ToString(), "-1" })
            Assert.Null(ProviderErrorClassifier.ParseRetryAfter(value, DateTimeOffset.UtcNow));
        Assert.Contains("25 MB", ProviderError.PayloadTooLarge(c, 25_000_000).UserMessage);
    }

    /// <summary>Every textual form of an error: Swift's userMessage, description, localizedDescription and reflection.</summary>
    static string[] Texts(ProviderError e) => [e.UserMessage, e.ToString(), new ProviderException(e).Message, new ProviderException(e).ToString(), JsonSerializer.Serialize(e)];

    [Fact]
    public void NoSecretsInMessages()
    {
        // Bodies that (maliciously or by echo) contain the key and transcript must not leak into messages.
        var echo = "{\"error\": {\"code\": 400, \"message\": \"bad key " + FakeSecret + " for text " + TranscriptSecret + "\", \"status\": \"INVALID_ARGUMENT\"}}";
        var errors = new List<ProviderError>();
        foreach (var provider in Enum.GetValues<ProviderID>())
            foreach (var status in new[] { 200, 400, 401, 402, 403, 404, 408, 412, 413, 429, 500, 503, 504 })
                errors.Add(ProviderErrorClassifier.Classify(provider, Result(status, echo, new() { ["Retry-After"] = "5" })));
        errors.AddRange([ProviderError.Cancelled, ProviderError.InvalidConfiguration(ProviderID.CustomAPI), ProviderError.ContentBlocked(ProviderID.CustomAPI), ProviderError.PayloadTooLarge(ProviderID.CustomAPI, null)]);
        foreach (var error in errors)
            Assert.DoesNotContain(Texts(error), t => t.Contains(FakeSecret) || t.Contains("FAKEKEY") || t.Contains("Nightingale"));
        var parsed = Assert.Throws<ProviderException>(() => OpenAIChatFormat.Parse(ProviderID.CustomAPI, Result(400, echo)));
        Assert.True(!parsed.ToString().Contains(FakeSecret) && !parsed.ToString().Contains("Nightingale") && parsed.InnerException is null, "thrown parse error redacted");
        // A request or response printed by mistake shows neither the key, the transcript nor the body.
        var request = OpenAIChatFormat.MakeRequest(ProviderUrl.Parse("https://api.example.com/v1/chat/completions"), Key, "m", new CleanupMessages("s", TranscriptSecret), null);
        Assert.True(!request.ToString().Contains(FakeSecret) && !request.ToString().Contains("Nightingale"));
        Assert.DoesNotContain("Nightingale", Result(400, echo).ToString());
    }

    static OpenAICompatibleCleanupProvider Custom() => new("https://api.example.com/v1", "m", Key, ProviderID.CustomAPI);

    [Fact]
    public async Task TransportBehaviour()
    {
        // Deadline.
        var slow = Custom() with { Transport = new MockTransport(Result(200, ChatSuccess), delaySeconds: 5), Timeout = 1 };
        var started = Stopwatch.StartNew();
        await ExpectAsync(ProviderError.Timeout(ProviderID.CustomAPI), () => slow.CleanupAsync(new CleanupMessages("s", "u")));
        Assert.True(started.Elapsed.TotalSeconds < 3, "deadline enforced promptly");

        // A transport that ignores cancellation still meets the deadline (Swift races a timer).
        var stubborn = Custom() with { Transport = new MockTransport(() => { Thread.Sleep(4000); return Result(200, ChatSuccess); }), Timeout = 1 };
        started.Restart();
        await ExpectAsync(ProviderError.Timeout(ProviderID.CustomAPI), () => stubborn.CleanupAsync(new CleanupMessages("s", "u")));
        Assert.True(started.Elapsed.TotalSeconds < 3, "deadline enforced without the transport's help");

        // Cancellation through the token.
        var cancellable = Custom() with { Transport = new MockTransport(Result(200, ChatSuccess), delaySeconds: 5) };
        using var cts = new CancellationTokenSource();
        var task = cancellable.CleanupAsync(new CleanupMessages("s", "u"), cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        var cancelStart = Stopwatch.StartNew();
        await ExpectAsync(ProviderError.Cancelled, () => task);
        Assert.True(cancelStart.Elapsed.TotalSeconds < 2, "cancellation prompt");
        await ExpectAsync(ProviderError.Cancelled, () => Custom().CleanupAsync(new CleanupMessages("s", "u"), cts.Token));

        // Transport errors.
        var offline = Custom() with { Transport = MockTransport.Failing(TransportError.NotConnectedToInternet) };
        await ExpectAsync(ProviderError.Network(ProviderID.CustomAPI), () => offline.CleanupAsync(new CleanupMessages("s", "u")));
    }

    [Fact]
    public void SilenceRule()
    {
        Assert.True(AudioFormat.ShouldSkipForSilence(0), "digital silence skipped");
        Assert.True(AudioFormat.ShouldSkipForSilence(0.005f), "below -40 dBFS skipped");
        Assert.False(AudioFormat.ShouldSkipForSilence(0.2f), "normal speech level sent");
        Assert.False(AudioFormat.ShouldSkipForSilence(0.02f, 2), "quiet but present speech sent");
        Assert.True(AudioFormat.ShouldSkipForSilence(0.5f, 0.1), "accidental tap skipped");
        Assert.True(AudioFormat.ShouldSkipForSilence(float.NaN) && AudioFormat.ShouldSkipForSilence(float.PositiveInfinity), "non-finite level skipped");
        Assert.True(!AudioFormat.ShouldSkipForSilence(0.05f, threshold: 0.04f) && AudioFormat.ShouldSkipForSilence(0.03f, threshold: 0.04f), "custom threshold");
    }

    // MARK: Host classification (URL validation)

    static EndpointLocality? Loc(string s) { try { return LocalEndpoint.Locality(LocalEndpoint.Normalize(s)); } catch (ProviderException) { return null; } }

    [Fact]
    public void HostClassification()
    {
        string[] onDevice = ["http://localhost:11434", "http://LOCALHOST:11434", "HTTP://LocalHost:1234/v1", "http://127.0.0.1:8080", "http://127.255.255.254", "http://127.0.0.1",
                             "https://localhost/v1/", "http://[::1]:1234", "http://[0:0:0:0:0:0:0:1]:8000", "http://[0000::0001]", "http://[::0001]:11434", "  http://localhost:11434/  "];
        foreach (var s in onDevice) Assert.True(Loc(s) == EndpointLocality.OnDevice, $"on-device: {s}");
        (string, string, bool)[] leaves = [
            ("http://127.0.0.1.evil.com", "127.0.0.1.evil.com", false), ("http://localhost.example:11434", "localhost.example", false), ("http://LOCALHOST.EXAMPLE", "localhost.example", false),
            ("http://localhost.", "localhost.", false), ("http://192.168.1.20:11434", "192.168.1.20", false), ("http://10.0.0.5:8000", "10.0.0.5", false), ("http://172.16.0.9", "172.16.0.9", false),
            ("http://mac-studio.local:11434", "mac-studio.local", false), ("https://gpu.example.com/v1", "gpu.example.com", true), ("http://128.0.0.1", "128.0.0.1", false),
            ("http://127.1", "127.1", false), ("http://2130706433", "2130706433", false), ("http://0177.0.0.1", "0177.0.0.1", false), ("http://[::ffff:127.0.0.1]", "::ffff:127.0.0.1", false),
            ("http://[::2]", "::2", false), ("http://[fe80::1]:11434", "fe80::1", false), ("http://[FD00::1]", "fd00::1", false), ("http://localhost-ai", "localhost-ai", false)];
        foreach (var (s, host, encrypted) in leaves) Assert.True(Loc(s) == EndpointLocality.LeavesDevice(host, encrypted), $"leaves device: {s} → {Loc(s)}");
        string[] rejected = ["", "   ", "localhost:11434", "ftp://localhost", "file:///tmp/sock", "javascript:alert(1)", "ws://localhost:8080",
            "http://user:pass@localhost:11434", "http://user@localhost", "http://localhost@evil.com", "http://evil.com\\@localhost",
            "http://0.0.0.0:11434", "http://[::]:8000", "http://[0:0:0:0:0:0:0:0]", "http://localhost:11434/?q=1", "http://localhost#x",
            "http://", "http://loc%61lhost", "http://[::1%25lo0]:1234", "http://exa mple.com", "http://localhost:99999",
            "http://localhost/../x", "http://local_host", "http://[::1::2]", "http://[12345::1]", "http://.localhost", "http://exаmple.com"];
        foreach (var s in rejected) Expect(ProviderError.InvalidConfiguration(ProviderID.LocalServer), () => LocalEndpoint.Normalize(s));
        Assert.True(LocalEndpoint.IsLoopback("[::1]") && LocalEndpoint.IsLoopback("::1") && LocalEndpoint.IsLoopback("LOCALHOST") && LocalEndpoint.IsLoopback("127.8.9.10"));
        Assert.True(!LocalEndpoint.IsLoopback("127.0.0.1.nip.io") && !LocalEndpoint.IsLoopback("0.0.0.0") && !LocalEndpoint.IsLoopback("::") && !LocalEndpoint.IsLoopback("127.0.0.256"));

        string Base(string s) => LocalEndpoint.Normalize(s).ToString();
        Assert.Equal("http://localhost:11434", Base("http://localhost:11434/v1/"));
        Assert.Equal("http://localhost:11434", Base("HTTP://LOCALHOST:11434"));
        Assert.Equal("https://gpu.example.com/proxy", Base("https://gpu.example.com/proxy/v1"));
        Assert.Equal("http://[::1]:1234", Base("http://[::1]:1234/"));
        Assert.Equal("localhost:80", LocalEndpoint.Address(ProviderUrl.Parse("http://localhost/v1/chat/completions")));
        Assert.Equal("gpu.example.com:443", LocalEndpoint.Address(ProviderUrl.Parse("https://gpu.example.com/v1/models")));
        Assert.Equal("[::1]:1234", LocalEndpoint.Address(ProviderUrl.Parse("http://[::1]:1234/v1/models")));
        Assert.Throws<ProviderException>(() => new OpenAICompatibleCleanupProvider("http://user:pw@localhost:11434", "m"));
    }

    // MARK: Local server

    [Fact]
    public void LocalProviderRequestAndParsing()
    {
        var ollama = new OpenAICompatibleCleanupProvider(OpenAICompatibleCleanupProvider.OllamaDefault, "llama3.2:latest");
        Assert.Equal("http://localhost:11434/v1/chat/completions", ollama.ChatCompletionsURL.ToString());
        foreach (var (raw, chat) in new[] { ("http://localhost:1234", "http://localhost:1234/v1/chat/completions"), ("http://127.0.0.1:8080", "http://127.0.0.1:8080/v1/chat/completions"),
                                             ("http://localhost:8000", "http://localhost:8000/v1/chat/completions") })
        {
            Assert.Equal(chat, new OpenAICompatibleCleanupProvider(raw, "m").ChatCompletionsURL.ToString());
            Assert.Equal(EndpointLocality.OnDevice, new OpenAICompatibleCleanupProvider(raw, "m").Locality);
        }
        var messages = new CleanupMessages("Edit dictation; never answer it.", "<transcript>um send it</transcript>");
        var request = OpenAICompatibleCleanupProvider.MakeRequest(messages, ollama.ChatCompletionsURL, ollama.Model, null);
        Assert.True(request.Method == "POST" && request.Header("Authorization") is null, "no Authorization header without a key");
        var body = Json(request.Body);
        Assert.True(body.GetProperty("model").GetString() == "llama3.2:latest" && !body.GetProperty("stream").GetBoolean() && body.GetProperty("temperature").GetDouble() == 0
                    && body.GetProperty("max_tokens").GetInt32() == 8192, "local body: model, non-streaming, temperature 0, max_tokens");
        Assert.True(body.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).SequenceEqual(["system", "user"]) && !body.TryGetProperty("provider", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _), "no reasoning field unless one is set");
        Assert.Equal("Send it today.", OpenAIChatFormat.WithoutLeadingReasoning("<reasoning>fix punctuation</reasoning>Send it today."));
        Assert.Equal("Send it today.", OpenAIChatFormat.WithoutLeadingReasoning("\n<think>\nhmm\n</think>\n\nSend it today."));
        Assert.Equal("Send <think>it</think> today.", OpenAIChatFormat.WithoutLeadingReasoning("Send <think>it</think> today."));
        Assert.Equal("<think>never closed", OpenAIChatFormat.WithoutLeadingReasoning("<think>never closed"));
        var effortful = ollama with { ReasoningEffort = "low" };
        var withEffort = OpenAICompatibleCleanupProvider.MakeRequest(messages, ollama.ChatCompletionsURL, ollama.Model, null, reasoningEffort: effortful.ReasoningEffort);
        Assert.Equal("low", Json(withEffort.Body).GetProperty("reasoning_effort").GetString());
        Assert.Null(((OpenAICompatibleCleanupProvider)effortful.ForMeetingNotes()).ReasoningEffort);
        var keyed = OpenAICompatibleCleanupProvider.MakeRequest(messages, ollama.ChatCompletionsURL, "m", Key);
        Assert.Equal($"Bearer {FakeSecret}", keyed.Header("Authorization"));
        Expect(ProviderError.InvalidConfiguration(ProviderID.LocalServer), () => OpenAICompatibleCleanupProvider.MakeRequest(messages, ollama.ChatCompletionsURL, "", null));
        Expect(ProviderError.InvalidConfiguration(ProviderID.LocalServer), () => OpenAICompatibleCleanupProvider.MakeRequest(messages, ollama.ChatCompletionsURL, "a\nb", null));
        OpenAICompatibleCleanupProvider.MakeRequest(messages, ollama.ChatCompletionsURL, "lmstudio-community/qwen2.5-7b@q4_k_m", null);

        Assert.Equal("Please send the report.", OpenAICompatibleCleanupProvider.Parse(Result(200, LocalChatOK)));
        Expect(ProviderError.OutputTruncated(ProviderID.LocalServer), () => OpenAICompatibleCleanupProvider.Parse(Result(200, LocalChatLength)));
        Expect(ProviderError.MalformedResponse(ProviderID.LocalServer), () => OpenAICompatibleCleanupProvider.Parse(Result(200, "<html>")));
        Expect(ProviderError.Rejected(ProviderID.LocalServer, 404), () => OpenAICompatibleCleanupProvider.Parse(Result(404, OllamaModelNotFound)));
        Expect(ProviderError.Authentication(ProviderID.LocalServer), () => OpenAICompatibleCleanupProvider.Parse(Result(401, """{"error":{"code":401,"message":"Invalid API Key","type":"authentication_error"}}""")));
        Expect(ProviderError.Server(ProviderID.LocalServer, 503), () => OpenAICompatibleCleanupProvider.Parse(Result(503, """{"error":{"code":503,"message":"Loading model","type":"unavailable_error"}}""")));
        // Redirect rule: a 3xx is never followed and never used.
        Expect(ProviderError.Rejected(ProviderID.LocalServer, 307), () => OpenAICompatibleCleanupProvider.Parse(Result(307, "", new() { ["Location"] = "https://evil.example/v1/chat/completions" })));

        Assert.Equal(["llama3.2:latest", "qwen2.5:7b"], OpenAICompatibleCleanupProvider.ParseModelList(Result(200, OllamaV1Models)));
        Assert.Equal(["gemma-3-4b-it-Q4_K_M.gguf"], OpenAICompatibleCleanupProvider.ParseModelList(Result(200, LlamaServerModels)));
        Assert.Equal(["llama3.2:latest", "mistral:7b"], OpenAICompatibleCleanupProvider.ParseOllamaTags(Result(200, OllamaTags)));
        Assert.Empty(OpenAICompatibleCleanupProvider.ParseModelList(Result(200, """{"object":"list","data":[]}""")));
        Assert.Equal(["a"], OpenAICompatibleCleanupProvider.ParseModelList(Result(200, """{"data":[{"id":"a"},{"id":"a"},{"id":"bad\nid"},{"name":"x"}]}""")));
        Expect(ProviderError.MalformedResponse(ProviderID.LocalServer), () => OpenAICompatibleCleanupProvider.ParseModelList(Result(200, OllamaTags)));
        Expect(ProviderError.MalformedResponse(ProviderID.LocalServer), () => OpenAICompatibleCleanupProvider.ParseOllamaTags(Result(200, OllamaV1Models)));

        // Disclosure by host.
        var local = ollama.Disclosure;
        Assert.True(local.StaysOnThisMac && local.LeavesDevice.Count == 0 && local.Warning is null && local.DestinationHost == "localhost:11434", "loopback disclosure: nothing leaves");
        Assert.True(local.Summary.Contains("nothing leaves this Mac") && local.StaysOnDevice.Contains("the transcript text"));
        var lan = new OpenAICompatibleCleanupProvider("http://192.168.1.20:11434", "m");
        Assert.True(!lan.Disclosure.StaysOnThisMac && string.Concat(lan.Disclosure.LeavesDevice).Contains("transcript") && lan.Disclosure.Summary.Contains("to 192.168.1.20"));
        Assert.True(lan.Disclosure.Warning!.Contains("not encrypted") && lan.Disclosure.Summary.Contains("not encrypted") && !lan.Disclosure.Warning.Contains("API key"));
        var lanKeyed = lan with { ApiKey = Key };
        Assert.True(lanKeyed.Disclosure.Warning!.Contains("API key") && !lanKeyed.Disclosure.Summary.Contains(FakeSecret));
        // HTTPS is required off loopback for keys: plain http off this computer sends no key.
        Assert.True(lanKeyed.SendableKey is null && lanKeyed.MakeGetRequest(lanKeyed.ModelsURL).Header("Authorization") is null, "plain http to LAN sends no key");
        Assert.Contains("not sent over plain http", lanKeyed.Disclosure.Warning);
        // The static builders apply the rule themselves, so a caller passing a raw key cannot leak it.
        var plainLAN = ProviderUrl.Parse("http://192.168.1.5:8080/v1/chat/completions");
        Assert.True(OpenAIChatFormat.MakeRequest(plainLAN, Key, "m", new CleanupMessages("s", "u"), null).Header("Authorization") is null
                    && OpenAITranscriptionFormat.MakeRequest(plainLAN, Key, "m", new AudioPayload([1]), "b").Header("Authorization") is null, "request builders drop a key bound for plain http off this computer");
        var lanTLS = new OpenAICompatibleCleanupProvider("https://192.168.1.20:11434", "m") { ApiKey = Key };
        Assert.Equal($"Bearer {FakeSecret}", lanTLS.MakeGetRequest(lanTLS.ModelsURL).Header("Authorization"));
        var loopKeyed = ollama with { ApiKey = Key };
        Assert.Equal($"Bearer {FakeSecret}", loopKeyed.MakeGetRequest(loopKeyed.ModelsURL).Header("Authorization"));
        Assert.Contains("to studio.local", new OpenAICompatibleCleanupProvider("http://studio.local:1234", "m").Disclosure.Summary);
        var remote = new OpenAICompatibleCleanupProvider("https://gpu.example.com", "m");
        Assert.True(remote.Disclosure.Warning is null && remote.Disclosure.Summary.Contains("gpu.example.com"));
        var trick = new OpenAICompatibleCleanupProvider("http://127.0.0.1.evil.com:11434", "m");
        Assert.True(!trick.Disclosure.StaysOnThisMac && trick.Disclosure.Warning is not null, "127.0.0.1.evil.com is not on-device");
    }

    [Fact]
    public void LocalErrorsAndRedaction()
    {
        var chat = ProviderUrl.Parse("http://localhost:11434/v1/chat/completions");
        var refusedError = new TransportException(TransportError.CannotConnectToHost);
        Assert.Equal(ProviderError.ServerNotRunning(ProviderID.LocalServer, "localhost:11434"), ProviderErrorClassifier.Classify(ProviderID.LocalServer, refusedError, chat));
        Assert.Equal(ProviderError.ServerNotRunning(ProviderID.LocalServer, "[::1]:1234"), ProviderErrorClassifier.Classify(ProviderID.LocalServer, refusedError, ProviderUrl.Parse("http://[::1]:1234/v1/models")));
        Assert.Equal(ProviderError.ServerNotRunning(ProviderID.LocalServer, "the configured address"), ProviderErrorClassifier.Classify(ProviderID.LocalServer, refusedError));
        var cloud = ProviderUrl.Parse("https://api.example.com/v1/models");
        Assert.Equal(ProviderError.Network(ProviderID.CustomAPI), ProviderErrorClassifier.Classify(ProviderID.CustomAPI, refusedError, cloud));
        Assert.True(ProviderErrorClassifier.Classify(ProviderID.CustomAPI, new TransportException(TransportError.NetworkConnectionLost), chat) == ProviderError.Network(ProviderID.LocalServer)
                    && ProviderErrorClassifier.Classify(ProviderID.CustomAPI, new TransportException(TransportError.TimedOut), chat) == ProviderError.Timeout(ProviderID.LocalServer));
        Assert.True(!ProviderError.Network(ProviderID.LocalServer).UserMessage.Contains("internet")
                    && ProviderErrorClassifier.Classify(ProviderID.CustomAPI, new TransportException(TransportError.TimedOut), cloud) == ProviderError.Timeout(ProviderID.CustomAPI));
        foreach (var custom in new[] { ProviderID.CustomAPI, ProviderID.CustomCleanup, ProviderID.CustomTranscription })
        {
            Assert.Equal(ProviderError.ServerNotRunning(custom, "localhost:11434"), ProviderErrorClassifier.Classify(custom, refusedError, chat));
            Assert.Equal(ProviderError.ServerNotRunning(custom, "[::1]:1234"), ProviderErrorClassifier.Classify(custom, refusedError, ProviderUrl.Parse("http://[::1]:1234/v1/models")));
            Assert.Equal(ProviderError.Network(custom), ProviderErrorClassifier.Classify(custom, refusedError, ProviderUrl.Parse("https://llm.example.com/v1/models")));
            Assert.Equal(ProviderError.Network(custom), ProviderErrorClassifier.Classify(custom, refusedError, ProviderUrl.Parse("http://192.168.1.20:8000/v1/models")));
        }
        Assert.Equal(ProviderError.Network(ProviderID.LocalServer), ProviderErrorClassifier.Classify(ProviderID.LocalServer, new TransportException(TransportError.CannotFindHost), chat));
        Assert.Equal(ProviderError.Timeout(ProviderID.LocalServer), ProviderErrorClassifier.Classify(ProviderID.LocalServer, new TransportException(TransportError.TimedOut), chat));
        var refused = ProviderError.ServerNotRunning(ProviderID.LocalServer, "localhost:11434");
        Assert.Equal("No local model server is running at localhost:11434. Start it, or check the address in Settings.", refused.UserMessage);
        Assert.True(refused.Disposition == SessionDisposition.PreserveRetryLater && refused.Provider == ProviderID.LocalServer);
        Assert.True(ProviderError.Network(ProviderID.LocalServer).UserMessage.Contains("local model server") && !ProviderError.Network(ProviderID.LocalServer).UserMessage.Contains("internet"));
        Assert.Contains("loading", ProviderError.Timeout(ProviderID.LocalServer).UserMessage);
        Assert.Contains("server address", ProviderError.InvalidConfiguration(ProviderID.LocalServer).UserMessage);

        // Hostile responses echoing key and transcript must not leak.
        var echo = "{\"error\": {\"code\": 400, \"message\": \"bad key " + FakeSecret + " for text " + TranscriptSecret + "\"}}";
        var errors = new List<ProviderError> { refused };
        foreach (var status in new[] { 200, 307, 400, 401, 403, 404, 413, 429, 500, 503 }) errors.Add(ProviderErrorClassifier.Classify(ProviderID.LocalServer, Result(status, echo)));
        foreach (var error in errors) Assert.DoesNotContain(Texts(error), t => t.Contains(FakeSecret) || t.Contains("Nightingale"));
        var keyed = new OpenAICompatibleCleanupProvider("http://localhost:11434", "m") { ApiKey = Key };
        Assert.True(!keyed.ToString().Contains(FakeSecret) && !JsonSerializer.Serialize(keyed).Contains(FakeSecret), "local provider dump redacted");
    }

    [Fact]
    public async Task LocalTransportBehaviour()
    {
        var messages = new CleanupMessages("s", "u");
        var ok = new MockTransport(Result(200, LocalChatOK));
        var provider = new OpenAICompatibleCleanupProvider("http://localhost:11434", "llama3.2:latest") { Transport = ok };
        Assert.Equal("Please send the report.", await provider.CleanupAsync(messages));
        Assert.True(ok.Requests.Count == 1 && ok.Requests[0].TimeoutInterval == 120, "one request, 120 s default timeout");
        Assert.Equal("localhost", ok.Requests[0].Url.Host);

        await (provider with { Timeout = 10_000 }).CleanupAsync(messages);
        Assert.Equal(300, ok.Requests[^1].TimeoutInterval);
        await (provider with { Timeout = 0 }).CleanupAsync(messages);
        Assert.Equal(1, ok.Requests[^1].TimeoutInterval);

        var failing = new MockTransport(Result(503, """{"error":{"code":503,"message":"Loading model"}}"""));
        await ExpectAsync(ProviderError.Server(ProviderID.LocalServer, 503), () => (provider with { Transport = failing }).CleanupAsync(messages));
        Assert.Single(failing.Requests); // no retry, no fallback

        var refused = MockTransport.Failing(TransportError.CannotConnectToHost);
        var e = await Assert.ThrowsAsync<ProviderException>(() => (provider with { Transport = refused }).CleanupAsync(new CleanupMessages("s", TranscriptSecret)));
        Assert.Equal(ProviderError.ServerNotRunning(ProviderID.LocalServer, "localhost:11434"), e.Error);
        Assert.DoesNotContain("Nightingale", e.ToString());
        Assert.Single(refused.Requests);

        // Discovery: /v1/models first; Ollama /api/tags only on 404.
        var modern = new RoutingTransport(new() { ["/v1/models"] = Result(200, OllamaV1Models) });
        Assert.Equal(["llama3.2:latest", "qwen2.5:7b"], await (provider with { Transport = modern }).DiscoverModelsAsync());
        Assert.True(modern.Requests.Select(r => r.Url.Path).SequenceEqual(["/v1/models"]) && modern.Requests[0].Method == "GET");
        var legacy = new RoutingTransport(new() { ["/api/tags"] = Result(200, OllamaTags) });
        Assert.Equal(["llama3.2:latest", "mistral:7b"], await (provider with { Transport = legacy }).DiscoverModelsAsync());
        Assert.True(legacy.Requests.Select(r => r.Url.Path).SequenceEqual(["/v1/models", "/api/tags"]) && legacy.Requests.All(r => r.Url.Host == "localhost" && r.Body is null));
        var unauthorized = new RoutingTransport(new() { ["/v1/models"] = Result(401, """{"error":"unauthorized"}""") });
        await ExpectAsync(ProviderError.Authentication(ProviderID.LocalServer), () => (provider with { Transport = unauthorized }).DiscoverModelsAsync());
        Assert.Single(unauthorized.Requests);
        var keyedRoutes = new RoutingTransport(new() { ["/v1/models"] = Result(200, OllamaV1Models) });
        await (provider with { Transport = keyedRoutes, ApiKey = Key }).ValidateAsync();
        Assert.True(keyedRoutes.Requests[0].Header("Authorization") == $"Bearer {FakeSecret}" && keyedRoutes.Requests[0].Url.Path == "/v1/models");
    }

    [Fact]
    public async Task CustomTranscription()
    {
        var audio = new AudioPayload([1, 2, 3]);
        foreach (var address in new[] { "https://speech.example/tenant/v1", "http://192.168.1.20:8000/v1", "http://localhost:8000/v1" })
        {
            var provider = new OpenAICompatibleTranscriptionProvider(address, "custom/whisper:latest", Key);
            var request = provider.MakeRequest(audio, "synthetic-boundary");
            Assert.Equal(LocalEndpoint.Normalize(address) + "/v1/audio/transcriptions", request.Url.ToString());
            var body = Encoding.UTF8.GetString(request.Body!);
            Assert.True(body.Contains("custom/whisper:latest") && !body.Contains("Murmur") && !body.Contains("name=\"prompt\""));
            Assert.Equal(!address.Contains("192.168"), request.Header("Authorization") is not null);
            Assert.Equal(address.Contains("localhost"), provider.Disclosure.StaysOnThisMac);
            Assert.True(provider.Disclosure.StaysOnDevice.Any(s => s.Contains("dictionary")) && !string.Concat(provider.Disclosure.LeavesDevice).Contains("spelling"));
            var transport = new MockTransport(Result(200, """{"text":"Synthetic custom provider transcript."}"""));
            provider = provider with { Transport = transport };
            Assert.Equal("Synthetic custom provider transcript.", await provider.TranscribeAsync(audio));
            Assert.Single(transport.Requests);
            await provider.ValidateAsync();
            Assert.Equal(provider.Server.ModelsURL, transport.Requests[^1].Url);
        }
        var refusing = MockTransport.Failing(TransportError.CannotConnectToHost);
        foreach (var (address, expected) in new[] { ("http://localhost:8000", "No local model server is running at localhost:8000. Start it, or check the address in Settings."),
                                                    ("https://speech.example/v1", ProviderError.Network(ProviderID.CustomAPI).UserMessage) })
        {
            var provider = new OpenAICompatibleTranscriptionProvider(address, "whisper-1") { Transport = refusing };
            var e = await Assert.ThrowsAsync<ProviderException>(() => provider.TranscribeAsync(audio));
            Assert.Equal(expected, e.Error.UserMessage);
        }
        foreach (var model in new[] { "whisper-1", "whisper-large-v3", "whisper-large-v3-turbo", "private-model-alias", "gpt-4o-transcribe" })
        {
            var fields = SplitMultipart(new OpenAICompatibleTranscriptionProvider("https://speech.example/v1", model, Key).MakeRequest(audio, "MODEL").Body!, "MODEL");
            Assert.True(!fields.ContainsKey("prompt") && fields["model"].SequenceEqual(B(model)));
        }
        var unkeyed = new OpenAICompatibleTranscriptionProvider("http://localhost:8000", "whisper-1");
        Assert.Null(unkeyed.MakeRequest(audio).Header("Authorization"));
        Assert.Throws<ProviderException>(() => new OpenAICompatibleTranscriptionProvider("https://user:password@example.com/v1", "m"));
        Assert.Throws<ProviderException>(() => new OpenAICompatibleTranscriptionProvider("https://example.com/v1", "bad\nmodel"));
        var broken = unkeyed with { Transport = new MockTransport(Result(200, """{"wrong":"response"}""")) };
        await ExpectAsync(ProviderError.MalformedResponse(ProviderID.CustomAPI), () => broken.TranscribeAsync(audio));
        var limit = OpenAICompatibleTranscriptionProvider.UploadLimitBytes;
        await ExpectAsync(ProviderError.PayloadTooLarge(ProviderID.CustomAPI, limit), () => broken.TranscribeAsync(new AudioPayload(new byte[limit + 1])));
    }

    [Fact]
    public void ExportedConstantsAreLoaded()
    {
        // shared/providers.json, from the Swift constants.
        Assert.Equal(16_000, AudioFormat.SampleRate);
        Assert.Equal(0.01f, AudioFormat.SilencePeak);
        Assert.Equal(8192, ProviderLimits.MaxOutputTokens);
        Assert.Equal(25_000_000, OpenAICompatibleTranscriptionProvider.UploadLimitBytes);
    }
}
