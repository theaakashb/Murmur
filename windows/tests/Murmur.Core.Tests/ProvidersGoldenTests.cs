// Compares Murmur.Core with golden/providers.json, which windows/golden/golden_providers.swift writes from the Mac's Swift.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>Shared helpers for the provider and route golden files.</summary>
internal static class ProviderGolden
{
    public const string Secret = "sk-synthetic-FAKEKEY0123456789";
    public static readonly APIKey Key = APIKey.Create(Secret)!;
    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_791_000_000);

    public static string? Str(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool Bool(JsonElement c, string name) => c.GetProperty(name).GetBoolean();
    public static bool IsNull(JsonElement c, string name) => !c.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;
    public static ProviderID Provider(string raw) => ProviderIDExtensions.FromRaw(raw) ?? throw new InvalidDataException(raw);

    public static double SwiftDouble(JsonElement v) => v.ValueKind == JsonValueKind.String
        ? v.GetString() switch { "inf" => double.PositiveInfinity, "-inf" => double.NegativeInfinity, "nan" => double.NaN, var s => throw new InvalidDataException(s) }
        : v.GetDouble();

    /// <summary>Asserts a C# error equals a golden error object (kind, provider, payload, message, disposition).</summary>
    public static void AssertError(JsonElement expected, ProviderError actual, string label)
    {
        Assert.True(Str(expected, "kind") == actual.Kind, $"{label}: kind {actual.Kind}, expected {Str(expected, "kind")}");
        Assert.Equal(Str(expected, "provider"), actual.Provider?.RawValue());
        Assert.Equal(Str(expected, "message"), actual.UserMessage);
        Assert.Equal(Str(expected, "disposition"), GoldenFiles.Camel(actual.Disposition));
        if (expected.TryGetProperty("retryAfter", out var r))
        {
            if (r.ValueKind == JsonValueKind.Null) Assert.Null(actual.RetryAfter);
            else Assert.Equal(SwiftDouble(r), actual.RetryAfter!.Value);
        }
        if (expected.TryGetProperty("limitBytes", out var l)) Assert.Equal(l.ValueKind == JsonValueKind.Null ? null : l.GetInt32(), actual.LimitBytes);
        if (expected.TryGetProperty("status", out var s)) Assert.Equal(s.ValueKind == JsonValueKind.Null ? null : s.GetInt64(), actual.Status);
        if (expected.TryGetProperty("address", out var a)) Assert.Equal(a.GetString(), actual.Address);
        if (expected.TryGetProperty("unavailable", out var u)) Assert.Equal(u.GetString(), GoldenFiles.Camel(actual.Unavailable!.Value));
    }

    /// <summary>Builds the C# error a golden error object describes.</summary>
    public static ProviderError MakeError(JsonElement e)
    {
        var kind = Enum.Parse<ProviderErrorKind>(Str(e, "kind")!, ignoreCase: true);
        if (kind == ProviderErrorKind.Cancelled) return ProviderError.Cancelled;
        var p = Provider(Str(e, "provider")!);
        int? Int(string n) => IsNull(e, n) ? null : e.GetProperty(n).GetInt32();
        return kind switch
        {
            ProviderErrorKind.Authentication => ProviderError.Authentication(p),
            ProviderErrorKind.Quota => ProviderError.Quota(p),
            ProviderErrorKind.RateLimited => ProviderError.RateLimited(p, IsNull(e, "retryAfter") ? null : SwiftDouble(e.GetProperty("retryAfter"))),
            ProviderErrorKind.Network => ProviderError.Network(p),
            ProviderErrorKind.Timeout => ProviderError.Timeout(p),
            ProviderErrorKind.PayloadTooLarge => ProviderError.PayloadTooLarge(p, Int("limitBytes")),
            ProviderErrorKind.Server => ProviderError.Server(p, Int("status")),
            ProviderErrorKind.MalformedResponse => ProviderError.MalformedResponse(p),
            ProviderErrorKind.ContentBlocked => ProviderError.ContentBlocked(p),
            ProviderErrorKind.Rejected => ProviderError.Rejected(p, Int("status")!.Value),
            ProviderErrorKind.InvalidConfiguration => ProviderError.InvalidConfiguration(p),
            ProviderErrorKind.ServerNotRunning => ProviderError.ServerNotRunning(p, Str(e, "address")!),
            _ => ProviderError.ModelUnavailable(p, Enum.Parse<OnDeviceModelUnavailable>(Str(e, "unavailable")!, ignoreCase: true)),
        };
    }

    /// <summary>Runs <paramref name="body"/> and checks it against a golden {"value": ...} or {"error": ...} outcome.</summary>
    public static void AssertOutcome<T>(JsonElement outcome, Func<T> body, Action<JsonElement, T> compare, string label)
    {
        if (outcome.TryGetProperty("error", out var expected))
        {
            var thrown = Assert.Throws<ProviderException>(() => body());
            AssertError(expected, thrown.Error, label);
        }
        else compare(outcome.GetProperty("value"), body());
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The multipart parts as [header, content], split as the generator does.</summary>
    public static List<string[]> Fields(byte[] body, string boundary)
    {
        var text = Encoding.UTF8.GetString(body);
        var parts = text.Split("--" + boundary).Skip(1);
        var output = new List<string[]>();
        foreach (var part in parts)
        {
            if (!part.StartsWith("\r\n")) continue;
            var end = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            var content = part[(end + 4)..];
            if (content.EndsWith("\r\n")) content = content[..^2];
            output.Add([part[2..end], content]);
        }
        return output;
    }

    /// <summary>Asserts a request equals a golden request object.</summary>
    public static void AssertRequest(JsonElement expected, ProviderRequest r, string? boundary = null)
    {
        Assert.Equal(Str(expected, "method"), r.Method);
        Assert.Equal(Str(expected, "url"), r.Url.ToString());
        Assert.Equal(expected.GetProperty("timeout").GetDouble(), r.TimeoutInterval);
        var headers = r.Headers.OrderBy(h => h.Key.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {(h.Value == "Bearer " + Secret ? "Bearer <key>" : h.Value)}").ToArray();
        Assert.Equal(expected.GetProperty("headers").EnumerateArray().Select(h => $"{h[0].GetString()}: {h[1].GetString()}").ToArray(), headers);
        var body = r.Body ?? [];
        if (expected.TryGetProperty("fields", out var fields))
        {
            Assert.Equal(fields.EnumerateArray().Select(f => f[0].GetString() + " | " + f[1].GetString()).ToArray(),
                         Fields(body, boundary!).Select(f => f[0] + " | " + f[1]).ToArray());
            Assert.Equal(expected.GetProperty("bodyLength").GetInt32(), body.Length);
            Assert.Equal(Str(expected, "bodySha256"), Sha(body));
        }
        else if (expected.TryGetProperty("json", out var json))
        {
            using var doc = JsonDocument.Parse(body);
            AssertJson(json, doc.RootElement, "body");
        }
        else Assert.Empty(body);
    }

    /// <summary>Deep JSON equality: same kinds, same keys in the same (sorted) order, numbers by value.</summary>
    public static void AssertJson(JsonElement expected, JsonElement actual, string path)
    {
        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: {actual.ValueKind}, expected {expected.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var e = expected.EnumerateObject().ToList();
                var a = actual.EnumerateObject().ToList();
                Assert.Equal(e.Select(p => p.Name), a.Select(p => p.Name));
                for (var i = 0; i < e.Count; i++) AssertJson(e[i].Value, a[i].Value, path + "." + e[i].Name);
                break;
            case JsonValueKind.Array:
                var ea = expected.EnumerateArray().ToList();
                var aa = actual.EnumerateArray().ToList();
                Assert.Equal(ea.Count, aa.Count);
                for (var i = 0; i < ea.Count; i++) AssertJson(ea[i], aa[i], $"{path}[{i}]");
                break;
            case JsonValueKind.String: Assert.Equal(expected.GetString(), actual.GetString()); break;
            case JsonValueKind.Number: Assert.Equal(expected.GetDouble(), actual.GetDouble()); break;
        }
    }

    public static object? Locality(EndpointLocality l) => l.IsOnDevice ? "onDevice" : $"{l.Host}|{l.Encrypted}";
    public static object? Locality(JsonElement l) => l.ValueKind == JsonValueKind.String ? l.GetString() : $"{l.GetProperty("host").GetString()}|{l.GetProperty("encrypted").GetBoolean()}";

    public static void AssertDisclosure(JsonElement e, DataDisclosure d)
    {
        Assert.Equal(Str(e, "provider"), d.Provider.RawValue());
        Assert.Equal(Str(e, "stage"), d.Stage.RawValue());
        Assert.Equal(Str(e, "host"), d.DestinationHost);
        Assert.Equal(GoldenFiles.Strings(e.GetProperty("leaves")), d.LeavesDevice);
        Assert.Equal(GoldenFiles.Strings(e.GetProperty("stays")), d.StaysOnDevice);
        Assert.Equal(Str(e, "retention"), d.RetentionNote);
        Assert.Equal(Str(e, "warning"), d.Warning);
        Assert.Equal(Bool(e, "staysOnThisMac"), d.StaysOnThisMac);
        Assert.Equal(Str(e, "summary"), d.Summary);
    }

    public static float[] Floats(JsonElement a) => [.. a.EnumerateArray().Select(x => (float)x.GetDouble())];
}

public class ProvidersGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("providers");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];
    static IEnumerable<object[]> Cases(string section) => GoldenFiles.Cases(Root, section, c => c.GetRawText());

    public static IEnumerable<object[]> Endpoints => Cases("endpoints");
    public static IEnumerable<object[]> Loopback => Cases("loopback");
    public static IEnumerable<object[]> Addresses => Cases("addresses");
    public static IEnumerable<object[]> TranscriptionRequests => Cases("transcriptionRequests");
    public static IEnumerable<object[]> ChatRequests => Cases("chatRequests");
    public static IEnumerable<object[]> MurmurCloudRequests => Cases("murmurCloudRequests");
    public static IEnumerable<object[]> Parse => Cases("parse");
    public static IEnumerable<object[]> Classify => Cases("classify");
    public static IEnumerable<object[]> RetryAfter => Cases("retryAfter");
    public static IEnumerable<object[]> TransportErrors => Cases("transportErrors");
    public static IEnumerable<object[]> Errors => Cases("errors");
    public static IEnumerable<object[]> ProviderIDs => Cases("providerIDs");
    public static IEnumerable<object[]> Disclosures => Cases("disclosures");
    public static IEnumerable<object[]> ApiKeys => Cases("apiKeys");
    public static IEnumerable<object[]> Reasoning => Cases("reasoning");
    public static IEnumerable<object[]> Helpers => Cases("helpers");

    static string S(JsonElement c, string n) => ProviderGolden.Str(c, n)!;

    [Theory, MemberData(nameof(Endpoints))]
    public void EndpointMatches(int i, string label)
    {
        var c = Case("endpoints", i);
        if (c.TryGetProperty("error", out var error))
        {
            var thrown = Assert.Throws<ProviderException>(() => LocalEndpoint.Normalize(S(c, "input")));
            ProviderGolden.AssertError(error, thrown.Error, label);
            return;
        }
        var url = LocalEndpoint.Normalize(S(c, "input"));
        Assert.Equal(S(c, "normalized"), url.ToString());
        Assert.Equal(ProviderGolden.Locality(c.GetProperty("locality")), ProviderGolden.Locality(LocalEndpoint.Locality(url)));
        Assert.Equal(S(c, "address"), LocalEndpoint.Address(url));
        Assert.Equal(ProviderGolden.Bool(c, "sendsKey"), LocalEndpoint.Sendable(ProviderGolden.Key, url) is not null);
    }

    [Theory, MemberData(nameof(Loopback))]
    public void LoopbackMatches(int i, string label)
    {
        var c = Case("loopback", i);
        Assert.True(ProviderGolden.Bool(c, "loopback") == LocalEndpoint.IsLoopback(S(c, "host")), label);
    }

    [Theory, MemberData(nameof(Addresses))]
    public void AddressMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("addresses", i);
        var url = ProviderUrl.Parse(S(c, "url"));
        Assert.Equal(S(c, "address"), LocalEndpoint.Address(url));
        Assert.Equal(ProviderGolden.Locality(c.GetProperty("locality")), ProviderGolden.Locality(LocalEndpoint.Locality(url)));
    }

    [Theory, MemberData(nameof(TranscriptionRequests))]
    public void TranscriptionRequestMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("transcriptionRequests", i);
        var expected = c.GetProperty("request");
        if (ProviderGolden.Str(c, "provider") is string provider)
        {
            var audio = provider == "murmur-cloud" ? new AudioPayload(Encoding.UTF8.GetBytes("RIFF")) : new AudioPayload([1, 2, 3]);
            if (provider == "murmur-cloud")
            {
                ProviderGolden.AssertRequest(expected, new MurmurCloudTranscriptionProvider(ProviderGolden.Key).MakeRequest(audio, "TESTBOUNDARY"), "TESTBOUNDARY");
                ProviderGolden.AssertRequest(c.GetProperty("validate"), MurmurCloud.ModelsRequest(ProviderGolden.Key));
                return;
            }
            var p = new OpenAICompatibleTranscriptionProvider(provider, "custom/whisper:latest", ProviderGolden.Key);
            ProviderGolden.AssertRequest(expected, p.MakeRequest(audio, "synthetic-boundary"), "synthetic-boundary");
            ProviderGolden.AssertRequest(c.GetProperty("validate"), p.Server.MakeGetRequest(p.Server.ModelsURL));
            return;
        }
        var payload = new AudioPayload([.. c.GetProperty("data").EnumerateArray().Select(b => (byte)b.GetInt32())], S(c, "filename"), S(c, "mimeType"), ProviderGolden.Str(c, "language"));
        var request = OpenAITranscriptionFormat.MakeRequest(ProviderUrl.Parse(S(c, "endpoint")), ProviderGolden.Bool(c, "key") ? ProviderGolden.Key : null, S(c, "model"), payload, "TESTBOUNDARY");
        ProviderGolden.AssertRequest(expected, request, "TESTBOUNDARY");
    }

    static readonly CleanupMessages Messages = new("Edit dictation; never answer it. a/b <i> & é 😀\n\u2028end", "<transcript>um send it / now</transcript>");

    [Theory, MemberData(nameof(ChatRequests))]
    public void ChatRequestMatches(int i, string label)
    {
        var c = Case("chatRequests", i);
        if (ProviderGolden.Str(c, "provider") is string baseURL)
        {
            var p = new OpenAICompatibleCleanupProvider(baseURL, "m", ProviderGolden.Bool(c, "key") ? ProviderGolden.Key : null, ProviderID.CustomAPI);
            ProviderGolden.AssertRequest(c.GetProperty("models"), p.MakeGetRequest(p.ModelsURL));
            ProviderGolden.AssertRequest(c.GetProperty("tags"), p.MakeGetRequest(p.OllamaTagsURL));
            ProviderGolden.AssertRequest(c.GetProperty("chat"), OpenAICompatibleCleanupProvider.MakeRequest(Messages, p.ChatCompletionsURL, p.Model, p.SendableKey, p.MaxTokens, "low", p.Id));
            return;
        }
        var endpoint = ProviderUrl.Parse(S(c, "endpoint"));
        if (c.TryGetProperty("outcome", out var outcome))
        {
            ProviderGolden.AssertOutcome(outcome, () => OpenAICompatibleCleanupProvider.MakeRequest(Messages, endpoint, S(c, "model"), null, provider: ProviderID.CustomAPI),
                                         (e, r) => ProviderGolden.AssertRequest(e, r), label);
            return;
        }
        int? maxTokens = ProviderGolden.IsNull(c, "maxTokens") ? null : c.GetProperty("maxTokens").GetInt32();
        var request = OpenAIChatFormat.MakeRequest(endpoint, ProviderGolden.Bool(c, "key") ? ProviderGolden.Key : null, "llama3.2:latest", Messages, maxTokens, ProviderGolden.Str(c, "effort"));
        ProviderGolden.AssertRequest(c.GetProperty("request"), request);
    }

    static readonly Dictionary<string, CleanupOptions> OptionSets = new()
    {
        ["professional"] = new(fillers: true, corrections: true, lists: false, punctuation: true, professional: true, spoken: true),
        ["polish"] = new(fillers: true, corrections: true, lists: true, punctuation: true, polish: true, spoken: true),
        ["polishNoSpoken"] = new(fillers: false, corrections: false, lists: true, punctuation: false, polish: true),
        ["professionalNoCorrections"] = new(corrections: false, professional: true),
        ["strict"] = new(punctuation: true),
    };

    [Theory, MemberData(nameof(MurmurCloudRequests))]
    public void MurmurCloudRequestMatches(int i, string label)
    {
        var c = Case("murmurCloudRequests", i);
        var context = ProviderGolden.Bool(c, "context") ? new ScreenContext { Selected = "Selected / <b>", Clipboard = "", Window = "Window — Zoë" } : null;
        var built = CleanupPrompt.Build(S(c, "transcript"), OptionSets[S(c, "options")], GoldenFiles.Strings(c.GetProperty("terms")), "N1", context, S(c, "notes"));
        Assert.NotNull(built.Input);
        ProviderGolden.AssertOutcome(c.GetProperty("outcome"), () => MurmurCloudCleanupFormat.MakeRequest(built.Input, ProviderGolden.Key), (e, r) => ProviderGolden.AssertRequest(e, r), label);
    }

    static void AssertSegments(JsonElement expected, IReadOnlyList<TranscriptSegment>? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        var e = expected.EnumerateArray().ToList();
        Assert.Equal(e.Count, actual.Count);
        double? D(JsonElement s, string n) => ProviderGolden.IsNull(s, n) ? null : s.GetProperty(n).GetDouble();
        for (var k = 0; k < e.Count; k++)
        {
            var s = e[k];
            Assert.Equal(new TranscriptSegment
            {
                Text = s.GetProperty("text").GetString()!, Start = D(s, "start"), End = D(s, "end"), AvgLogprob = D(s, "avgLogprob"), NoSpeechProb = D(s, "noSpeechProb"),
                CompressionRatio = D(s, "compressionRatio"), LoudWindows = ProviderGolden.IsNull(s, "loudWindows") ? null : s.GetProperty("loudWindows").GetInt32(),
                Dropped = s.GetProperty("dropped").GetBoolean(),
            }, actual[k]);
        }
    }

    [Theory, MemberData(nameof(Parse))]
    public void ParseMatches(int i, string label)
    {
        var c = Case("parse", i);
        var result = new HttpResult(c.GetProperty("status").GetInt32(), null, Encoding.UTF8.GetBytes(S(c, "body")));
        var outcome = c.GetProperty("outcome");
        void Strings(JsonElement e, IReadOnlyList<string> a) => Assert.Equal(GoldenFiles.Strings(e), a);
        void Text(JsonElement e, string a) => Assert.Equal(e.GetString(), a);
        switch (S(c, "fn"))
        {
            case "transcription":
                var provider = ProviderGolden.Provider(S(c, "provider"));
                IReadOnlyList<float>? levels = ProviderGolden.IsNull(c, "levels") ? null : ProviderGolden.Floats(c.GetProperty("levels"));
                ProviderGolden.AssertOutcome(outcome, () => OpenAITranscriptionFormat.ParseDetailed(provider, result, levels), (e, r) =>
                {
                    Assert.Equal(S(e, "text"), r.Text);
                    AssertSegments(e.GetProperty("segments"), r.Segments);
                    Assert.Equal(ProviderGolden.Str(e, "reason"), r.NoSpeechReason is { } reason ? GoldenFiles.Camel(reason) : null);
                }, label);
                break;
            case "chat":
                ProviderGolden.AssertOutcome(outcome, () => OpenAIChatFormat.Parse(ProviderGolden.Provider(S(c, "provider")), result), Text, label);
                break;
            case "murmurCloud":
                ProviderGolden.AssertOutcome(outcome, () => MurmurCloudCleanupFormat.Parse(result), Text, label);
                break;
            case "modelList":
                ProviderGolden.AssertOutcome(outcome, () => OpenAICompatibleCleanupProvider.ParseModelList(result), Strings, label);
                break;
            default:
                ProviderGolden.AssertOutcome(outcome, () => OpenAICompatibleCleanupProvider.ParseOllamaTags(result), Strings, label);
                break;
        }
    }

    [Theory, MemberData(nameof(Classify))]
    public void ClassifyMatches(int i, string label)
    {
        var c = Case("classify", i);
        var headers = c.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var result = new HttpResult(c.GetProperty("status").GetInt32(), headers, Encoding.UTF8.GetBytes(S(c, "body")));
        ProviderGolden.AssertError(c.GetProperty("error"), ProviderErrorClassifier.Classify(ProviderGolden.Provider(S(c, "provider")), result, ProviderGolden.Now), label);
    }

    [Theory, MemberData(nameof(RetryAfter))]
    public void RetryAfterMatches(int i, string label)
    {
        var c = Case("retryAfter", i);
        double? expected = ProviderGolden.IsNull(c, "seconds") ? null : c.GetProperty("seconds").GetDouble();
        Assert.True(expected == ProviderErrorClassifier.ParseRetryAfter(S(c, "value"), ProviderGolden.Now), label);
    }

    [Theory, MemberData(nameof(TransportErrors))]
    public void TransportErrorMatches(int i, string label)
    {
        var c = Case("transportErrors", i);
        Exception error = S(c, "code") == "cancellationError" ? new OperationCanceledException() : new TransportException(Enum.Parse<TransportError>(S(c, "code"), ignoreCase: true));
        var url = ProviderGolden.Str(c, "url") is string u ? ProviderUrl.Parse(u) : null;
        ProviderGolden.AssertError(c.GetProperty("error"), ProviderErrorClassifier.Classify(ProviderGolden.Provider(S(c, "provider")), error, url), label);
    }

    [Theory, MemberData(nameof(Errors))]
    public void ErrorMatches(int i, string label)
    {
        var c = Case("errors", i);
        ProviderGolden.AssertError(c, ProviderGolden.MakeError(c), label);
    }

    [Theory, MemberData(nameof(ProviderIDs))]
    public void ProviderIDMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("providerIDs", i);
        var id = (ProviderID)i;
        Assert.Equal(S(c, "raw"), id.RawValue());
        Assert.Equal(S(c, "displayName"), id.DisplayName());
        Assert.Equal(ProviderGolden.Bool(c, "runsProfessional"), id.RunsProfessional());
    }

    [Fact]
    public void ProviderIDsAreSwiftsAllCases() => Assert.Equal(Root.GetProperty("providerIDs").GetArrayLength(), Enum.GetValues<ProviderID>().Length);

    [Theory, MemberData(nameof(Disclosures))]
    public void DisclosureMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("disclosures", i);
        var key = c.TryGetProperty("key", out var k) && k.GetBoolean() ? ProviderGolden.Key : null;
        var expected = c.TryGetProperty("disclosure", out var d) ? d : default;
        switch (S(c, "kind"))
        {
            case "cleanup":
                ProviderGolden.AssertDisclosure(expected, new OpenAICompatibleCleanupProvider(S(c, "base"), "m", key, ProviderGolden.Provider(S(c, "provider"))).Disclosure);
                break;
            case "transcription":
                ProviderGolden.AssertDisclosure(expected, new OpenAICompatibleTranscriptionProvider(S(c, "base"), "whisper-1", key).Disclosure);
                break;
            case "murmurCloudCleanup": ProviderGolden.AssertDisclosure(expected, new MurmurCloudCleanupProvider(ProviderGolden.Key).Disclosure); break;
            case "murmurCloudTranscription": ProviderGolden.AssertDisclosure(expected, new MurmurCloudTranscriptionProvider(ProviderGolden.Key).Disclosure); break;
            default:
                var items = GoldenFiles.Strings(c.GetProperty("items"));
                Assert.Equal(S(c, "summary"), new DataDisclosure(ProviderID.CustomAPI, ProcessingStage.Cleanup, "h.example", items, [], "Note.", items.Length == 2 ? "Careful." : null).Summary);
                break;
        }
    }

    [Theory, MemberData(nameof(ApiKeys))]
    public void ApiKeyMatches(int i, string label)
    {
        var c = Case("apiKeys", i);
        var key = APIKey.Create(S(c, "raw"));
        Assert.True(ProviderGolden.Bool(c, "valid") == key is not null, label);
        Assert.Equal(ProviderGolden.Str(c, "secret"), key?.Secret);
    }

    [Theory, MemberData(nameof(Reasoning))]
    public void ReasoningMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("reasoning", i);
        Assert.Equal(S(c, "result"), OpenAIChatFormat.WithoutLeadingReasoning(S(c, "content")));
    }

    [Theory, MemberData(nameof(Helpers))]
    public void HelperMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("helpers", i);
        switch (S(c, "fn"))
        {
            case "loudWindows":
                Assert.Equal(c.GetProperty("result").GetInt32(), OpenAITranscriptionFormat.LoudWindows(ProviderGolden.Floats(c.GetProperty("levels")), c.GetProperty("start").GetDouble(), c.GetProperty("end").GetDouble()));
                break;
            case "peakPercentiles":
                var p = OpenAITranscriptionFormat.PeakPercentiles(ProviderGolden.Floats(c.GetProperty("levels")));
                if (ProviderGolden.IsNull(c, "result")) Assert.Null(p);
                else Assert.Equal(ProviderGolden.Floats(c.GetProperty("result")), new[] { p!.Value.P50, p.Value.P90 });
                break;
            case "sanitize":
                Assert.Equal(S(c, "result"), Multipart.Sanitize(S(c, "value")));
                break;
            default:
                var peakValue = c.GetProperty("peak");
                var peak = peakValue.ValueKind == JsonValueKind.String ? (peakValue.GetString() == "nan" ? float.NaN : float.PositiveInfinity) : (float)peakValue.GetDouble();
                double? duration = ProviderGolden.IsNull(c, "duration") ? null : c.GetProperty("duration").GetDouble();
                float? threshold = ProviderGolden.IsNull(c, "threshold") ? null : (float)c.GetProperty("threshold").GetDouble();
                Assert.Equal(c.GetProperty("result").GetBoolean(), AudioFormat.ShouldSkipForSilence(peak, duration, threshold));
                break;
        }
    }
}
