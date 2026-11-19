// Compares Murmur.Core with golden/routes.json, which windows/golden/golden_routes.swift writes from the Mac's Swift.
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>Answers every request with one canned result or transport failure, and records "METHOD url auth=yes|no" per request.</summary>
internal sealed class CannedTransport(int status, string body = "", TransportError? fail = null) : IHttpTransport
{
    readonly Lock gate = new();
    readonly List<string> paths = [];
    public readonly List<ProviderRequest> Requests = [];
    public string[] Paths { get { lock (gate) return [.. paths]; } }
    public int Count { get { lock (gate) return Requests.Count; } }
    public Task<HttpResult> SendAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        lock (gate) { paths.Add($"{request.Method} {request.Url} auth={(request.Header("Authorization") is null ? "no" : "yes")}"); Requests.Add(request); }
        if (fail is TransportError code) throw new TransportException(code);
        return Task.FromResult(new HttpResult(status, null, Encoding.UTF8.GetBytes(body)));
    }
}

/// <summary>A transcriber stub without a pre-check or segment detail.</summary>
internal sealed class SpyTranscriber(Func<string> response, ProviderID id = ProviderID.CustomAPI, double delaySeconds = 0) : ITranscriptionProvider
{
    int calls;
    public int Calls => Volatile.Read(ref calls);
    public ProviderID Id => id;
    public DataDisclosure Disclosure => new(Id, ProcessingStage.Transcription, "example.invalid", ["the recorded audio"], [], "test");
    public async Task<string> TranscribeAsync(AudioPayload audio, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        return response();
    }
    public Task ValidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>The real verbose_json parser on the route's audio levels, no pre-check.</summary>
internal sealed class VerboseTranscriber(string json) : IDetailedTranscriber
{
    public ProviderID Id => ProviderID.CustomAPI;
    public DataDisclosure Disclosure => new(Id, ProcessingStage.Transcription, "example.invalid", ["the recorded audio"], [], "test");
    public async Task<string> TranscribeAsync(AudioPayload audio, CancellationToken cancellationToken = default) => (await TranscribeDetailedAsync(audio, cancellationToken)).Text;
    public Task<TranscriptionResult> TranscribeDetailedAsync(AudioPayload audio, CancellationToken cancellationToken = default) =>
        Task.FromResult(OpenAITranscriptionFormat.ParseDetailed(ProviderID.CustomAPI, new HttpResult(200, null, Encoding.UTF8.GetBytes(json)), audio.Levels));
    public Task ValidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class RoutesGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("routes");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];
    static IEnumerable<object[]> Cases(string section) => GoldenFiles.Cases(Root, section, c => c.GetRawText());
    public static IEnumerable<object[]> Choices => Cases("choices");
    public static IEnumerable<object[]> Plans => Cases("plans");
    public static IEnumerable<object[]> Settings => Cases("settings");
    public static IEnumerable<object[]> Bindings => Cases("bindings");
    public static IEnumerable<object[]> KeyChecks => Cases("keyChecks");
    public static IEnumerable<object[]> Transcriptions => Cases("transcriptions");

    static readonly APIKey Key = ProviderGolden.Key;
    static string S(JsonElement c, string n) => ProviderGolden.Str(c, n)!;
    static string? KeyName(APIKey? k) => k is null ? null : k.Secret == ProviderGolden.Secret ? "key" : k.Secret;

    [Theory, MemberData(nameof(Choices))]
    public void ChoiceMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("choices", i);
        switch (S(c, "kind"))
        {
            case "cleanup":
                var choice = RouteRawValues.CleanupChoiceFrom(S(c, "raw"))!.Value;
                Assert.Equal(S(c, "raw"), choice.RawValue());
                Assert.Equal(S(c, "id"), choice.Id());
                Assert.Equal(S(c, "displayName"), choice.DisplayName());
                Assert.Equal(ProviderGolden.Bool(c, "cloudOnlyOK"), choice.CloudOnlyOK());
                Assert.Equal(S(c, "processingRoute"), choice.ProcessingRoute().RawValue());
                Assert.Equal(ProviderGolden.Str(c, "providerID"), choice.ProviderID()?.RawValue());
                Assert.Equal(ProviderGolden.Str(c, "credentialID"), choice.CredentialID()?.RawValue());
                break;
            case "transcription":
                Assert.Equal(S(c, "raw"), RouteRawValues.TranscriptionRouteFrom(S(c, "raw"))!.Value.RawValue());
                break;
            default:
                var s = new RouteSettings { CustomAPIBaseURL = "c", CustomTranscriptionAPIBaseURL = "t" };
                Assert.Equal(ProviderGolden.Str(c, "result"), s.ApiBaseURL(ProviderGolden.Provider(S(c, "provider"))));
                break;
        }
    }

    [Fact]
    public void ChoicesAreSwiftsAllCases()
    {
        var all = Root.GetProperty("choices").EnumerateArray().ToList();
        Assert.Equal(all.Where(c => S(c, "kind") == "cleanup").Select(c => S(c, "raw")), Enum.GetValues<CleanupChoice>().Select(c => c.RawValue()));
        Assert.Equal(all.Where(c => S(c, "kind") == "transcription").Select(c => S(c, "raw")), Enum.GetValues<TranscriptionRoute>().Select(t => t.RawValue()));
    }

    static RouteSettings SettingsFrom(JsonElement s) => new()
    {
        Transcription = RouteRawValues.TranscriptionRouteFrom(S(s, "transcription"))!.Value,
        Cleanup = RouteRawValues.CleanupChoiceFrom(S(s, "cleanup"))!.Value,
        CustomAPIBaseURL = S(s, "customAPIBaseURL"), CustomTranscriptionAPIBaseURL = S(s, "customTranscriptionAPIBaseURL"), CustomAPIModel = S(s, "customAPIModel"),
        CustomTranscriptionModel = S(s, "customTranscriptionModel"), CustomAPIReasoningEffort = S(s, "customAPIReasoningEffort"),
    };

    static void AssertSettings(JsonElement e, RouteSettings s) => Assert.Equal(SettingsFrom(e), s);

    static void AssertTranscriber(JsonElement e, ITranscriptionProvider? t)
    {
        if (e.ValueKind == JsonValueKind.Null) { Assert.Null(t); return; }
        Assert.NotNull(t);
        Assert.Equal(S(e, "id"), t.Id.RawValue());
        Assert.Equal(ProviderGolden.Str(e, "model"), t.ModelID);
        Assert.Equal(ProviderGolden.Bool(e, "precheck"), t is ISilencePrecheckedTranscriber);
        switch (t)
        {
            case OpenAICompatibleTranscriptionProvider p: Assert.Equal("custom", S(e, "type")); Assert.Equal(S(e, "endpoint"), p.Endpoint.ToString()); Assert.Equal(ProviderGolden.Str(e, "key"), KeyName(p.Server.ApiKey)); break;
            case MurmurCloudTranscriptionProvider m: Assert.Equal("murmurCloud", S(e, "type")); Assert.Equal(S(e, "endpoint"), m.Endpoint.ToString()); Assert.Equal(ProviderGolden.Str(e, "key"), KeyName(m.ApiKey)); break;
            default: Assert.Fail("unexpected transcriber type"); break;
        }
    }

    static void AssertCleaner(JsonElement e, ICleanupProvider? c)
    {
        if (e.ValueKind == JsonValueKind.Null) { Assert.Null(c); return; }
        Assert.NotNull(c);
        Assert.Equal(S(e, "id"), c.Id.RawValue());
        Assert.Equal(ProviderGolden.Str(e, "model"), c.ModelID);
        switch (c)
        {
            case OpenAICompatibleCleanupProvider p:
                Assert.Equal("custom", S(e, "type"));
                Assert.Equal(S(e, "base"), p.BaseURL.ToString());
                Assert.Equal(ProviderGolden.Str(e, "key"), KeyName(p.ApiKey));
                Assert.Equal(ProviderGolden.Str(e, "effort"), p.ReasoningEffort);
                Assert.Equal(e.GetProperty("timeout").GetDouble(), p.Timeout);
                Assert.Equal(e.GetProperty("notesTimeout").GetDouble(), ((OpenAICompatibleCleanupProvider)p.ForMeetingNotes()).Timeout);
                break;
            case MurmurCloudCleanupProvider m: Assert.Equal("murmurCloud", S(e, "type")); Assert.Equal(ProviderGolden.Str(e, "key"), KeyName(m.ApiKey)); break;
            case LocalModelCleanupProvider l:
                Assert.Equal("localModel", S(e, "type"));
                ProviderGolden.AssertDisclosure(e.GetProperty("disclosure"), l.Disclosure);
                Assert.Equal(e.GetProperty("shape").EnumerateArray().Select(x => x.GetBoolean()), new[] { l.Shape.TakesContext, l.Shape.KeepsCorrectionsAttached, l.Shape.TakesPresets });
                Assert.Equal(e.GetProperty("sectionTarget").GetInt32(), l.Shape.SectionTarget);
                break;
            default: Assert.Fail("unexpected cleaner type"); break;
        }
    }

    static readonly Dictionary<string, HashSet<ProviderID>> KeySets = new()
    {
        ["none"] = [], ["all"] = [.. Enum.GetValues<ProviderID>()], ["murmurCloud"] = [ProviderID.MurmurCloud], ["custom"] = [ProviderID.CustomTranscription, ProviderID.CustomCleanup],
    };

    [Theory, MemberData(nameof(Plans))]
    public void PlanMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("plans", i);
        var keys = KeySets[S(c, "keys")];
        var asked = new List<string>();
        var plan = RoutePlan.Make(SettingsFrom(c.GetProperty("settings")), ProviderGolden.Bool(c, "sends"), id => { asked.Add(id.RawValue()); return keys.Contains(id) ? Key : null; });
        Assert.Equal(GoldenFiles.Strings(c.GetProperty("asked")), asked);
        AssertTranscriber(c.GetProperty("transcriber"), plan.Transcriber);
        AssertCleaner(c.GetProperty("cleaner"), plan.Cleaner);
        Assert.Equal(ProviderGolden.Str(c, "refusal"), plan.Refusal);
        Assert.Equal(ProviderGolden.Bool(c, "usesLocalModel"), plan.UsesLocalModel);
        var stages = c.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(stages.Count, plan.Stages.Count);
        for (var k = 0; k < stages.Count; k++) ProviderGolden.AssertDisclosure(stages[k], plan.Stages[k]);
        Assert.Equal(ProviderGolden.Str(c, "transcriptionSummary"), DisclosureText.Summary(plan, ProcessingStage.Transcription));
        Assert.Equal(ProviderGolden.Str(c, "cleanupSummary"), DisclosureText.Summary(plan, ProcessingStage.Cleanup));
    }

    [Theory, MemberData(nameof(Settings))]
    public void SettingsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("settings", i);
        if (c.TryGetProperty("defaults", out var defaults))
        {
            AssertSettings(defaults, new RouteSettings());
            Assert.Equal(S(c, "placeholderAPIURL"), RouteSettings.PlaceholderAPIURL);
            Assert.Equal(S(c, "placeholderModel"), RouteSettings.PlaceholderModel);
            return;
        }
        var store = new InMemorySettingsStore(c.GetProperty("stored").EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetString()));
        var loaded = RouteSettings.Load(store);
        AssertSettings(c.GetProperty("loaded"), loaded);
        var saved = new InMemorySettingsStore();
        loaded.Save(saved);
        Assert.Equal(c.GetProperty("saved").EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetString()), saved.Snapshot());
    }

    [Theory, MemberData(nameof(Bindings))]
    public void BindingMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("bindings", i);
        if (c.TryGetProperty("endpoint", out var endpoint))
        {
            string? result;
            try { result = CustomAPIKeyBinding.Endpoint(endpoint.GetString()!); } catch (ProviderException) { result = null; }
            Assert.Equal(ProviderGolden.Str(c, "result"), result);
            return;
        }
        if (c.TryGetProperty("preference", out var preference))
        {
            Assert.Equal(S(c, "result"), CustomAPIKeyBinding.PreferenceFor(ProviderGolden.Provider(preference.GetString()!)));
            return;
        }
        var provider = ProviderGolden.Provider(S(c, "provider"));
        var defaults = new InMemorySettingsStore();
        var store = new InMemoryCredentialStore();
        if (ProviderGolden.Str(c, "savedAt") is string savedAt) { try { CustomAPIKeyBinding.Save(Key, savedAt, defaults, store, provider); } catch (ProviderException) { } }
        else store.Save(Key, provider);
        var readAt = S(c, "readAt");
        // "read" records a provider error as "invalid: <kind>"; "readKey" as Swift prints the error itself, its user message.
        static string Describe(Func<APIKey?> read, bool message)
        {
            try { return read() == ProviderGolden.Key ? "key" : "nil"; }
            catch (CredentialException e) { return e.Error.Description; }
            catch (ProviderException e) { return message ? e.Error.UserMessage : "invalid: " + e.Error.Kind; }
        }
        Assert.Equal(S(c, "read"), Describe(() => CustomAPIKeyBinding.Read(readAt, defaults, store, provider), false));
        Assert.Equal(ProviderGolden.Str(c, "stored"), defaults.Get(CustomAPIKeyBinding.PreferenceFor(provider)) as string);
        Assert.Equal(ProviderGolden.Bool(c, "needsConfirmation"), CustomAPIKeyBinding.NeedsConfirmation(readAt, defaults, provider));
        var routes = new RouteSettings { CustomAPIBaseURL = readAt, CustomTranscriptionAPIBaseURL = readAt };
        Assert.Equal(S(c, "readKey"), Describe(() => CustomAPIKeyBinding.ReadKey(provider, routes, defaults, store), true));
    }

    static readonly Dictionary<string, TransportError> Failures = new()
    {
        ["cannotConnectToHost"] = TransportError.CannotConnectToHost, ["timedOut"] = TransportError.TimedOut, ["notConnectedToInternet"] = TransportError.NotConnectedToInternet,
    };

    [Theory, MemberData(nameof(KeyChecks))]
    public async Task KeyCheckMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("keyChecks", i);
        if (c.TryGetProperty("validated", out var validated))
        {
            Assert.Equal(S(c, "message"), KeyText.Validated(ProviderGolden.Provider(validated.GetString()!)));
            return;
        }
        if (c.TryGetProperty("discover", out var discover))
        {
            var mock = new CannedTransport(c.GetProperty("status").GetInt32(), S(c, "body"));
            var found = await KeyCheck.DiscoverModelsAsync(discover.GetString()!, Key, mock);
            if (ProviderGolden.IsNull(c, "models")) Assert.Null(found.Models); else Assert.Equal(GoldenFiles.Strings(c.GetProperty("models")), found.Models!);
            if (ProviderGolden.IsNull(c, "error")) Assert.Null(found.Error); else Assert.Equal(S(c.GetProperty("error"), "message"), found.Error!.UserMessage);
            Assert.Equal(S(c, "message"), KeyCheck.DiscoveryMessage(found));
            Assert.Equal(GoldenFiles.Strings(c.GetProperty("requests")), mock.Paths);
            return;
        }
        var transport = c.TryGetProperty("failName", out var fail) ? new CannedTransport(0, "", Failures[fail.GetString()!]) : new CannedTransport(c.GetProperty("status").GetInt32(), S(c, "body"));
        var message = await KeyCheck.RunAsync(ProviderGolden.Provider(S(c, "provider")), ProviderGolden.Bool(c, "key") ? Key : null, S(c, "base"), transport);
        Assert.Equal(S(c, "message"), message);
        Assert.Equal(GoldenFiles.Strings(c.GetProperty("requests")), transport.Paths);
        Assert.DoesNotContain(ProviderGolden.Secret, message);
    }

    static int Bits(float f) => (int)BitConverter.SingleToUInt32Bits(f);
    static int? Bits(JsonElement c, string n) => ProviderGolden.IsNull(c, n) ? null : (int)c.GetProperty(n).GetInt64();

    [Theory, MemberData(nameof(Transcriptions))]
    public async Task TranscriptionMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("transcriptions", i);
        if (c.TryGetProperty("messageOf", out var of))
        {
            CloudTranscriptionOutcome outcome = of.GetString() switch
            {
                "cancelled" => new CloudTranscriptionOutcome.Cancelled(),
                "failedDiscard" => new CloudTranscriptionOutcome.Failed("M.", false),
                _ => new CloudTranscriptionOutcome.Failed(CloudTranscription.UnreadableAudio, true),
            };
            Assert.Equal(ProviderGolden.Str(c, "message"), CloudTranscription.Message(outcome));
            return;
        }
        if (c.TryGetProperty("raw", out var raw))
        {
            var (o, d) = await CloudTranscription.RunDetailedAsync(new SpyTranscriber(() => "Hello."), Encoding.UTF8.GetBytes(raw.GetString()!));
            Assert.Equal(S(c, "outcome"), o == new CloudTranscriptionOutcome.Transcript("Hello.") ? "transcript" : o.ToString());
            Assert.Equal(S(c, "logLine"), d.NoSpeechLogLine);
            Assert.Equal(S(c, "precheck"), GoldenFiles.Camel(d.Precheck));
            return;
        }
        var data = WAVAudio.EncodePCM16(CloudAudioGoldenTests.Samples(c.GetProperty("recipe")), c.GetProperty("rate").GetInt32());
        var payload = S(c, "payload");
        var status = c.GetProperty("status").GetInt32();
        ITranscriptionProvider provider = S(c, "kind") switch
        {
            "spy" => new SpyTranscriber(() => payload == "text" ? S(c, "extra") : throw new ProviderException(S(c, "extra") switch
            {
                "authentication" => ProviderError.Authentication(ProviderID.CustomAPI),
                "rateLimited" => ProviderError.RateLimited(ProviderID.CustomAPI, 30),
                _ => ProviderError.Cancelled,
            })),
            "verbose" => new VerboseTranscriber(payload),
            "custom" => new OpenAICompatibleTranscriptionProvider("https://stt.example.test", "whisper-large-v3-turbo", Key) { Transport = new CannedTransport(status, payload) },
            _ => new MurmurCloudTranscriptionProvider(Key) { Transport = new CannedTransport(status, payload) },
        };
        var (outcome2, detail) = await CloudTranscription.RunDetailedAsync(provider, data);
        var e = c.GetProperty("outcome");
        CloudTranscriptionOutcome expected =
            e.TryGetProperty("transcript", out var t) ? new CloudTranscriptionOutcome.Transcript(t.GetString()!)
            : e.TryGetProperty("noSpeech", out var n) ? new CloudTranscriptionOutcome.NoSpeech(n.GetBoolean())
            : e.TryGetProperty("failed", out var f) ? new CloudTranscriptionOutcome.Failed(f.GetString()!, e.GetProperty("keepAudio").GetBoolean())
            : new CloudTranscriptionOutcome.Cancelled();
        Assert.Equal(expected, outcome2);
        Assert.Equal(ProviderGolden.Str(c, "message"), CloudTranscription.Message(outcome2));
        var d2 = c.GetProperty("detail");
        Assert.Equal(ProviderGolden.IsNull(d2, "clipSeconds") ? null : d2.GetProperty("clipSeconds").GetDouble(), detail.ClipSeconds);
        Assert.Equal(Bits(d2, "peakP50"), detail.PeakP50 is float p50 ? Bits(p50) : null);
        Assert.Equal(Bits(d2, "peakP90"), detail.PeakP90 is float p90 ? Bits(p90) : null);
        Assert.Equal(Bits(d2, "silencePeak"), detail.SilencePeak is float sp ? Bits(sp) : null);
        Assert.Equal(S(d2, "precheck"), GoldenFiles.Camel(detail.Precheck));
        Assert.Equal(ProviderGolden.Str(d2, "reason"), detail.NoSpeechReason is { } r ? GoldenFiles.Camel(r) : null);
        if (ProviderGolden.IsNull(d2, "segments")) Assert.Null(detail.Segments);
        else Assert.Equal(d2.GetProperty("segments").EnumerateArray().Select(s => $"{s[0].GetString()}|{s[1].GetBoolean()}|{(s[2].ValueKind == JsonValueKind.Null ? "-" : s[2].GetInt32())}"),
                          detail.Segments!.Select(s => $"{s.Text}|{s.Dropped}|{(s.LoudWindows is int w ? w : "-")}"));
        Assert.Equal(S(d2, "logLine"), detail.NoSpeechLogLine);
    }

    /// <summary>check_routes.swift:144-146: every destination setting stays on this machine when restoring. The C# route keys, plus
    /// the cleanupRoute preference, must be exactly the Mac's DataBackup.destinationSettings, legacy names included.</summary>
    [Fact]
    public void EveryDestinationSettingStaysOnThisMachineWhenRestoring()
    {
        var d = Root.GetProperty("destination");
        string[] keys = [RouteSettings.Key.Transcription, RouteSettings.Key.LegacyCloudTranscriber, RouteSettings.Key.Cleanup, RouteSettings.Key.CustomAPIBaseURL,
                         RouteSettings.Key.CustomTranscriptionAPIBaseURL, RouteSettings.Key.LegacyServerURL];
        Assert.Equal(GoldenFiles.Strings(d.GetProperty("routeKeys")), keys);
        Assert.Equal(GoldenFiles.Strings(d.GetProperty("destinationSettings")).ToHashSet(), keys.Append(d.GetProperty("cleanupRouteKey").GetString()!).ToHashSet());
        Assert.Equal("cleanupRoute", d.GetProperty("cleanupRouteKey").GetString());
    }

}
