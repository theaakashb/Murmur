// Port of app/OpenAICompatible.swift.
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Murmur.Core;

// OpenAI-compatible speech-to-text and chat formats, the custom STT provider, LocalEndpoint and the OpenAI-compatible cleanup provider.

public static class OpenAITranscriptionFormat
{
    /// <summary>Only these hosts document verbose_json (literal in OpenAICompatible.swift's makeRequest).</summary>
    static readonly string[] VerboseJSONHosts = ["api.groq.com", "api.openai.com"];

    public static ProviderRequest MakeRequest(ProviderUrl endpoint, APIKey? apiKey, string model, AudioPayload audio, string boundary)
    {
        // Whisper models return per-segment confidence in verbose_json; gpt-4o-transcribe accepts only json.
        // Only Groq and OpenAI document it; other compatible servers keep json so a rejected format never fails the route.
        var documented = VerboseJSONHosts.Contains(endpoint.Host.ToLowerInvariant());
        var format = documented && model.ToLowerInvariant().Contains("whisper") ? "verbose_json" : "json";
        var fields = new List<(string, string)> { ("model", model), ("response_format", format), ("temperature", "0") };
        if (!string.IsNullOrEmpty(audio.Language)) fields.Add(("language", audio.Language));
        // Whisper treats prompt text as prior transcript context and can echo it. Apply dictionary
        // spellings after transcription; compatible endpoints have no shared structured vocabulary field.
        var request = new ProviderRequest { Method = "POST", Url = endpoint };
        if (LocalEndpoint.Sendable(apiKey, endpoint) is APIKey key) request = request.WithHeader("Authorization", $"Bearer {key.Secret}");
        return request.WithHeader("Content-Type", $"multipart/form-data; boundary={boundary}").WithHeader("Accept", "application/json")
            with { Body = Multipart.Body(boundary, fields, "file", audio.Filename, audio.MimeType, audio.Data) };
    }

    // shortcut: Whisper's own defaults (no_speech_threshold, logprob_threshold), not yet calibrated against real dictations; tune with the Mac.
    public static readonly double NoSpeechThreshold = ProvidersData.Value.OpenAI.NoSpeechThreshold;
    public static readonly double LogprobThreshold = ProvidersData.Value.OpenAI.LogprobThreshold;
    // shortcut: a segment with under 300 ms of sound above the silence level is taken as invented; tune with the Mac.
    public static readonly int MinLoudWindows = ProvidersData.Value.OpenAI.MinLoudWindows;

    /// <summary>100 ms windows between <paramref name="start"/> and <paramref name="end"/> seconds whose peak reaches the silence level.
    /// Time past the end of the audio has no sound.</summary>
    public static int LoudWindows(IReadOnlyList<float> levels, double start, double end)
    {
        int from = Math.Max(0, Saturate(start * 10)), to = Math.Min(levels.Count, Saturate(Math.Ceiling(end * 10)));
        var count = 0;
        for (var i = from; i < to; i++) if (levels[i] >= AudioFormat.SilencePeak) count++;
        return count;
    }

    /// <summary>Swift's <c>Int(double)</c> truncation, clamped where Swift would trap.</summary>
    static int Saturate(double v) => double.IsNaN(v) ? 0 : (int)Math.Clamp(Math.Truncate(v), int.MinValue, int.MaxValue);

    /// <summary><c>{"text": "..."}</c>. An empty string (silence) is valid; a missing <c>text</c> is not. With verbose_json
    /// <c>segments</c>, trailing segments are dropped when the model itself marks them as no speech, or when
    /// <paramref name="levels"/> shows the audio under them is quiet; the words are never inspected. Partial segment data keeps <c>text</c>.</summary>
    public static string Parse(ProviderID provider, HttpResult result, IReadOnlyList<float>? levels = null) => ParseDetailed(provider, result, levels).Text;

    /// <summary>Median and 90th-percentile of the clip's window peaks, for the log and for History.</summary>
    public static (float P50, float P90)? PeakPercentiles(IReadOnlyList<float> levels)
    {
        if (levels.Count == 0) return null;
        var sorted = levels.Order().ToArray();
        return (sorted[sorted.Length / 2], sorted[sorted.Length * 9 / 10]);
    }

    /// <summary><see cref="Parse"/> with the segments it saw, so History can keep what the model heard and what was dropped.</summary>
    public static TranscriptionResult ParseDetailed(ProviderID provider, HttpResult result, IReadOnlyList<float>? levels = null)
    {
        if (result.Status is < 200 or >= 300) throw new ProviderException(ProviderErrorClassifier.Classify(provider, result));
        if (Json.Object(result.Body) is not JsonElement obj) throw new ProviderException(ProviderError.MalformedResponse(provider));
        if (Json.TryGet(obj, "error", out _)) throw new ProviderException(ProviderErrorClassifier.Classify(provider, result));
        if (Json.String(obj, "text") is not string text) throw new ProviderException(ProviderError.MalformedResponse(provider));
        if (Json.Objects(obj, "segments") is not List<JsonElement> raw) return new TranscriptionResult(text);
        var segments = new List<(string Text, bool Silent, bool Unsure)>();
        var details = new List<TranscriptSegment>();
        foreach (var segment in raw)
        {
            if (Json.String(segment, "text") is not string words || Number(segment, "no_speech_prob") is not double noSpeech
                || Number(segment, "avg_logprob") is not double logprob) return new TranscriptionResult(text);
            var silent = noSpeech > NoSpeechThreshold && logprob < LogprobThreshold;
            var ratio = Number(segment, "compression_ratio");
            int? loudCount = null;
            if (levels is not null && Number(segment, "start") is double start && Number(segment, "end") is double end)
            {
                var loud = LoudWindows(levels, start, end);
                loudCount = loud;
                silent = silent || loud < MinLoudWindows;
            }
            segments.Add((words, silent, logprob < LogprobThreshold));
            details.Add(new TranscriptSegment { Text = words, Start = Number(segment, "start"), End = Number(segment, "end"), AvgLogprob = logprob,
                                                NoSpeechProb = noSpeech, CompressionRatio = ratio, LoudWindows = loudCount });
        }
        // shortcut: Swift also logs content-free score lines (logTail) to the timing log; Murmur.Core has no logger yet. Add with Processing in M2.
        // Groq reports no_speech_prob 0 even for a transcript invented from room noise, but then every
        // segment is low-confidence. Treated as no speech, the recording stays for a retry.
        if (segments.Count > 0 && segments.All(s => s.Unsure))
            return new TranscriptionResult("", [.. details.Select(d => d with { Dropped = true })], NoSpeechReason.LowConfidence);
        var tail = segments.Count - 1;
        while (tail >= 0 && segments[tail].Silent) tail--;
        if (segments.Count == 0 || !segments[^1].Silent) return new TranscriptionResult(text, details);
        for (var i = tail + 1; i < details.Count; i++) details[i] = details[i] with { Dropped = true };
        // Whisper segment text carries its own leading space.
        var kept = string.Concat(segments.Take(tail + 1).Select(s => s.Text));
        return new TranscriptionResult(SwiftText.TrimWhitespaceAndNewlines(kept), details, tail < 0 ? NoSpeechReason.InsufficientLoudness : null);
    }

    static double? Number(JsonElement obj, string name) => Json.TryGet(obj, name, out var v) ? Json.SwiftDouble(v) : null;
}

/// <summary>OpenAI-style <c>POST …/chat/completions</c>, shared by the custom cleanup endpoint and local servers.</summary>
public static class OpenAIChatFormat
{
    internal static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary><paramref name="bearer"/> null, or any key for a cleartext remote <paramref name="endpoint"/>, sends no Authorization header.
    /// <paramref name="reasoningEffort"/> null leaves the field out, for servers that reject it. Keys are written sorted, as Swift's JSONEncoder does.</summary>
    public static ProviderRequest MakeRequest(ProviderUrl endpoint, APIKey? bearer, string model, CleanupMessages messages, int? maxTokens, string? reasoningEffort = null)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Writer))
        {
            w.WriteStartObject();
            if (maxTokens is int max) w.WriteNumber("max_tokens", max);
            w.WriteStartArray("messages");
            foreach (var (role, content) in new[] { ("system", messages.System), ("user", messages.User) })
            {
                w.WriteStartObject(); w.WriteString("content", content); w.WriteString("role", role); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("model", model);
            if (reasoningEffort is not null) w.WriteString("reasoning_effort", reasoningEffort);
            w.WriteBoolean("stream", false);
            w.WriteNumber("temperature", 0);
            w.WriteEndObject();
        }
        var request = new ProviderRequest { Method = "POST", Url = endpoint };
        if (LocalEndpoint.Sendable(bearer, endpoint) is APIKey key) request = request.WithHeader("Authorization", $"Bearer {key.Secret}");
        return request.WithHeader("Content-Type", "application/json").WithHeader("Accept", "application/json") with { Body = buffer.ToArray() };
    }

    /// <summary><c>choices[0].message.content</c>. Errors can arrive with HTTP 200 (some gateways), so the <c>error</c>
    /// object is checked first. A <c>length</c> finish (truncated) is an error, <see cref="ProviderErrorKind.OutputTruncated"/>.</summary>
    public static string Parse(ProviderID provider, HttpResult result)
    {
        if (result.Status is < 200 or >= 300) throw new ProviderException(ProviderErrorClassifier.Classify(provider, result));
        if (Json.Object(result.Body) is not JsonElement obj) throw new ProviderException(ProviderError.MalformedResponse(provider));
        if (Json.TryGet(obj, "error", out _)) throw new ProviderException(ProviderErrorClassifier.Classify(provider, result));
        if (Json.Objects(obj, "choices") is not { Count: > 0 } choices) throw new ProviderException(ProviderError.MalformedResponse(provider));
        var first = choices[0];
        if (Json.TryGet(first, "error", out _)) throw new ProviderException(ProviderError.Server(provider, null));
        switch (Json.String(first, "finish_reason"))
        {
            case null or "stop": break;
            case "content_filter": throw new ProviderException(ProviderError.ContentBlocked(provider));
            case "length": throw new ProviderException(ProviderError.OutputTruncated(provider));
            default: throw new ProviderException(ProviderError.MalformedResponse(provider));
        }
        if (!Json.TryGet(first, "message", out var message) || message.ValueKind != JsonValueKind.Object || Json.String(message, "content") is not string content)
            throw new ProviderException(ProviderError.MalformedResponse(provider));
        return WithoutLeadingReasoning(content);
    }

    /// <summary>Some servers put a reasoning model's thinking at the start of the answer (Amazon Bedrock: <c>&lt;reasoning&gt;…&lt;/reasoning&gt;</c>;
    /// Ollama and llama-server: <c>&lt;think&gt;…&lt;/think&gt;</c>). Only a block that opens the content is removed.</summary>
    public static string WithoutLeadingReasoning(string content)
    {
        var characters = SwiftText.Characters(content);
        var start = characters.FindIndex(c => !SwiftText.IsWhitespace(c));
        if (start < 0) return content;
        foreach (var tag in new[] { "reasoning", "think" })
        {
            var open = SwiftText.Characters($"<{tag}>");
            if (characters.Count - start < open.Count || !open.Select((c, i) => characters[start + i] == c).All(x => x)) continue;
            var trimmed = string.Concat(characters.Skip(start));
            var close = trimmed.IndexOf($"</{tag}>", StringComparison.Ordinal);
            if (close < 0) continue;
            var after = SwiftText.Characters(trimmed[(close + tag.Length + 3)..]);
            return string.Concat(after.SkipWhile(SwiftText.IsWhitespace));
        }
        return content;
    }
}

/// <summary>Uses the same endpoint validation and credential transport policy as custom cleanup.</summary>
public sealed record OpenAICompatibleTranscriptionProvider : IDetailedTranscriber, ISilencePrecheckedTranscriber
{
    public OpenAICompatibleCleanupProvider Server { get; init; }
    public ProviderID Id => ProviderID.CustomAPI;
    public IHttpTransport Transport { get; init; } = HttpClientTransport.Default;
    /// <summary>Conservative client upload cap; the configured server may impose a smaller one.</summary>
    public static readonly int UploadLimitBytes = ProvidersData.Value.OpenAI.UploadLimitBytes;
    /// <summary>The upload deadline in seconds (a literal in the Swift provider).</summary>
    public const double Deadline = 75;

    /// <summary>Throws <see cref="ProviderException"/> with invalidConfiguration for a bad model or base URL.</summary>
    public OpenAICompatibleTranscriptionProvider(string baseURL, string model, APIKey? apiKey = null)
    {
        OpenAICompatibleCleanupProvider.RequireLocalModel(model, ProviderID.CustomAPI);
        Server = new OpenAICompatibleCleanupProvider(baseURL, model, apiKey, ProviderID.CustomAPI);
    }

    public ProviderUrl Endpoint => Server.BaseURL.Appending("/v1/audio/transcriptions");
    public string? ModelID => Server.Model;

    public DataDisclosure Disclosure
    {
        get
        {
            var locality = Server.Locality;
            if (locality.IsOnDevice)
                return new DataDisclosure(Id, ProcessingStage.Transcription, Server.Address, [], ["the recorded audio", "the dictionary", "history"],
                                          "Audio goes only to the server on this Mac; its logging settings apply.");
            return new DataDisclosure(Id, ProcessingStage.Transcription, locality.Host!, ["audio"], ["dictionary", "history"], "Server retention applies.",
                                      locality.Encrypted ? null : "Audio is sent over unencrypted http. Your API key is not sent; use https to send it.");
        }
    }

    public ProviderRequest MakeRequest(AudioPayload audio, string? boundary = null) =>
        OpenAITranscriptionFormat.MakeRequest(Endpoint, Server.SendableKey, Server.Model, audio, boundary ?? Multipart.MakeBoundary());

    public async Task<string> TranscribeAsync(AudioPayload audio, CancellationToken cancellationToken = default) =>
        (await TranscribeDetailedAsync(audio, cancellationToken).ConfigureAwait(false)).Text;

    public async Task<TranscriptionResult> TranscribeDetailedAsync(AudioPayload audio, CancellationToken cancellationToken = default)
    {
        if (audio.Data.Length > UploadLimitBytes) throw new ProviderException(ProviderError.PayloadTooLarge(Id, UploadLimitBytes));
        var result = await new ProviderHTTP(Id, Transport, Deadline).ExecuteAsync(MakeRequest(audio), cancellationToken).ConfigureAwait(false);
        return OpenAITranscriptionFormat.ParseDetailed(Id, result, audio.Levels);
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        var result = await new ProviderHTTP(Id, Transport, ProviderLimits.ValidationDeadline).ExecuteAsync(Server.MakeGetRequest(Server.ModelsURL), cancellationToken).ConfigureAwait(false);
        ProviderHTTP.ValidationResult(Id, result);
    }

    /// <summary>Whisper-style models invent "Thank you." or "Okay." for silence, so silent clips are not sent (Routes.swift).</summary>
    public bool ShouldSkip(float peakLevel, double? durationSeconds) => AudioFormat.ShouldSkipForSilence(peakLevel, durationSeconds);
}

/// <summary>Where a user-configured server address sends data: <see cref="OnDevice"/> (loopback, the request does not leave this computer),
/// or leaving it to <see cref="Host"/>, including LAN, <c>.local</c> and private addresses.</summary>
public sealed record EndpointLocality(bool IsOnDevice, string? Host = null, bool Encrypted = false)
{
    public static readonly EndpointLocality OnDevice = new(true);
    public static EndpointLocality LeavesDevice(string host, bool encrypted) => new(false, host, encrypted);
}

/// <summary>An http(s) URL as Swift's URL keeps it: the host exactly as written (never canonicalized, so <c>127.1</c> stays <c>127.1</c>),
/// an optional port and the path. <see cref="ToString"/> is Swift's <c>absoluteString</c>. Only the real transport converts it to a Uri.</summary>
public sealed record ProviderUrl(string Scheme, string Host, int? Port, string Path)
{
    public ProviderUrl Appending(string path) => this with { Path = Path + path };

    public override string ToString() => $"{Scheme}://{(Host.Contains(':') ? $"[{Host}]" : Host)}{(Port is int p ? ":" + p.ToString(CultureInfo.InvariantCulture) : "")}{Path}";

    /// <summary>Parses a fixed or test URL (no user info, query or fragment). Throws <see cref="FormatException"/>.</summary>
    public static ProviderUrl Parse(string s)
    {
        if (UrlComponents.Parse(s) is not { Scheme: string scheme, Host: string host } c || c.HasUserInfo || c.Query is not null || c.Fragment is not null)
            throw new FormatException("Not an absolute http URL.");
        return new ProviderUrl(scheme, host.StartsWith('[') ? host[1..^1] : host, c.Port, c.Path);
    }
}

/// <summary>The parts of URLComponents(string:) that LocalEndpoint reads, for the printable-ASCII strings it accepts. Null where URLComponents is nil.</summary>
internal sealed record UrlComponents(string? Scheme, string? Host, int? Port, string Path, string? Query, string? Fragment, bool HasUserInfo)
{
    public static UrlComponents? Parse(string s)
    {
        string? scheme = null;
        var colon = s.IndexOf(':');
        var delimiter = s.IndexOfAny(['/', '?', '#']);
        if (colon > 0 && (delimiter < 0 || colon < delimiter) && char.IsAsciiLetter(s[0]) && s[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            scheme = s[..colon];
            s = s[(colon + 1)..];
        }
        string? fragment = null, query = null;
        if (s.IndexOf('#') is var hash and >= 0) { fragment = s[(hash + 1)..]; s = s[..hash]; }
        if (s.IndexOf('?') is var question and >= 0) { query = s[(question + 1)..]; s = s[..question]; }
        if (!s.StartsWith("//")) return new UrlComponents(scheme, null, null, s, query, fragment, false);
        s = s[2..];
        var slash = s.IndexOf('/');
        var authority = slash < 0 ? s : s[..slash];
        var path = slash < 0 ? "" : s[slash..];
        var userInfo = authority.LastIndexOf('@');
        var hasUserInfo = userInfo >= 0;
        if (hasUserInfo) authority = authority[(userInfo + 1)..];
        string host, portText;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return null;
            host = authority[..(close + 1)];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0 && rest[0] != ':') return null;
            portText = rest.Length > 0 ? rest[1..] : "";
        }
        else
        {
            var portColon = authority.IndexOf(':');
            host = portColon < 0 ? authority : authority[..portColon];
            portText = portColon < 0 ? "" : authority[(portColon + 1)..];
        }
        int? port = null;
        if (portText.Length > 0)
        {
            if (!portText.All(char.IsAsciiDigit) || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p)) return null;
            port = p;
        }
        return new UrlComponents(scheme, host, port, path, query, fragment, hasUserInfo);
    }
}

/// <summary>Parses and classifies a user-supplied base URL for an OpenAI-compatible server.
/// On-device means exactly: <c>localhost</c>, IPv4 127.0.0.0/8 in strict dotted-decimal form, or IPv6 <c>::1</c> (any spelling,
/// with or without brackets). Everything else is treated as leaving the computer, including forms that may in fact reach loopback
/// (<c>127.1</c>, <c>2130706433</c>, <c>localhost.</c>, <c>::ffff:127.0.0.1</c>): over-disclosure is the safe direction. The unspecified
/// addresses <c>0.0.0.0</c> and <c>::</c> are rejected because they are listen addresses, not destinations.</summary>
public static class LocalEndpoint
{
    const string HostAllowed = "abcdefghijklmnopqrstuvwxyz0123456789.-";
    const string PathAllowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~/";

    /// <summary>Validates and normalizes a base URL. Accepts <c>http(s)://host[:port]</c>, an optional path prefix, and an optional
    /// trailing <c>/v1</c> (stripped, since endpoints are <c>{base}/v1/...</c>). Rejects other schemes, user info, query, fragment,
    /// percent-encoded or unusual hosts, and unspecified addresses. Throws invalidConfiguration(localServer).</summary>
    public static ProviderUrl Normalize(string raw)
    {
        var bad = new ProviderException(ProviderError.InvalidConfiguration(ProviderID.LocalServer));
        var trimmed = SwiftText.TrimWhitespaceAndNewlines(raw);
        if (trimmed.Length == 0 || SwiftText.Count(trimmed) > 2048 || !trimmed.All(c => c > 0x20 && c < 0x7F) || trimmed.Contains('@')
            || UrlComponents.Parse(trimmed) is not { } components || components.Scheme?.ToLowerInvariant() is not ("http" or "https")
            || components.HasUserInfo || components.Query is not null || components.Fragment is not null
            || components.Host is not { Length: > 0 } rawHost || rawHost.Contains('%')) throw bad;
        var scheme = components.Scheme.ToLowerInvariant();
        var host = BareHost(rawHost);
        if (host.Length == 0) throw bad;
        var isIPv6 = host.Contains(':');
        if (isIPv6)
        {
            if (IPv6Bytes(host) is not byte[] bytes || bytes.All(b => b == 0)) throw bad;
        }
        else
        {
            if (!host.All(HostAllowed.Contains) || host.StartsWith('.') || host.StartsWith('-') || host.Contains("..")) throw bad;
            if (IPv4Octets(host) is int[] octets && octets.All(o => o == 0)) throw bad;
        }
        if (components.Port is int port && port is < 1 or > 65535) throw bad;
        var path = components.Path;
        if (!path.All(PathAllowed.Contains) || path.Contains("..")) throw bad;
        path = path.TrimEnd('/');
        if (path.ToLowerInvariant().EndsWith("/v1", StringComparison.Ordinal)) path = path[..^3];
        path = path.TrimEnd('/');
        return new ProviderUrl(scheme, host, components.Port, path);
    }

    /// <summary>The key only where it may go: never in cleartext to a server off this computer. Every request builder applies this itself.</summary>
    public static APIKey? Sendable(APIKey? key, ProviderUrl url) => Locality(url) is { IsOnDevice: false, Encrypted: false } ? null : key;

    public static EndpointLocality Locality(ProviderUrl url)
    {
        var host = BareHost(url.Host);
        return IsLoopback(host) ? EndpointLocality.OnDevice : EndpointLocality.LeavesDevice(host, url.Scheme.ToLowerInvariant() == "https");
    }

    /// <summary>True only for the exact loopback forms listed on the type.</summary>
    public static bool IsLoopback(string rawHost)
    {
        var host = BareHost(rawHost);
        if (host == "localhost") return true;
        if (IPv4Octets(host) is int[] octets) return octets[0] == 127;
        if (host.Contains(':') && IPv6Bytes(host) is byte[] bytes) return bytes.Take(15).All(b => b == 0) && bytes[15] == 1;
        return false;
    }

    /// <summary>"host:port" for messages, with the scheme's default port when none is given.</summary>
    public static string Address(ProviderUrl url)
    {
        var host = BareHost(url.Host);
        var port = url.Port ?? (url.Scheme.ToLowerInvariant() == "https" ? 443 : 80);
        return $"{(host.Contains(':') ? $"[{host}]" : host)}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Lowercased host without IPv6 brackets.</summary>
    public static string BareHost(string host)
    {
        var h = host.ToLowerInvariant();
        return h.StartsWith('[') && h.EndsWith(']') && h.Length >= 2 ? h[1..^1] : h;
    }

    /// <summary>Strict dotted-decimal IPv4: four parts of 1–3 digits, each 0–255. Anything else is null.</summary>
    public static int[]? IPv4Octets(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4) return null;
        var octets = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (parts[i].Length is < 1 or > 3 || !parts[i].All(char.IsAsciiDigit)) return null;
            octets[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
            if (octets[i] > 255) return null;
        }
        return octets;
    }

    /// <summary>RFC 4291 text form (with <c>::</c> compression and an optional trailing dotted IPv4), no zone id.</summary>
    public static byte[]? IPv6Bytes(string host)
    {
        if (host.Length == 0 || SwiftText.Count(host) > 45 || host.Contains('%')) return null;
        var text = host;
        byte[] tail = [];
        var lastColon = text.LastIndexOf(':');
        if (lastColon >= 0 && text[(lastColon + 1)..].Contains('.'))
        {
            if (IPv4Octets(text[(lastColon + 1)..]) is not int[] v4) return null;
            tail = [.. v4.Select(o => (byte)o)];
            text = text[..(lastColon + 1)] + "0:0"; // two placeholder groups, replaced below
        }
        var halves = text.Split("::");
        if (halves.Length > 2) return null;
        static List<ushort>? Groups(string s)
        {
            if (s.Length == 0) return [];
            var output = new List<ushort>();
            foreach (var g in s.Split(':'))
            {
                if (g.Length is < 1 or > 4 || !ushort.TryParse(g, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) return null;
                output.Add(v);
            }
            return output;
        }
        List<ushort> words;
        if (halves.Length == 2)
        {
            if (Groups(halves[0]) is not { } head || Groups(halves[1]) is not { } rest || head.Count + rest.Count > 7) return null;
            words = [.. head, .. Enumerable.Repeat((ushort)0, 8 - head.Count - rest.Count), .. rest];
        }
        else
        {
            if (Groups(halves[0]) is not { Count: 8 } all) return null;
            words = all;
        }
        var bytes = words.SelectMany(w => new[] { (byte)(w >> 8), (byte)(w & 0xFF) }).ToArray();
        if (tail.Length > 0) tail.CopyTo(bytes, 12);
        return bytes;
    }
}

/// <summary>Cleanup through a user-run OpenAI-compatible server: Ollama, LM Studio, llama.cpp <c>llama-server</c> or vLLM.
/// <c>POST {base}/v1/chat/completions</c>, one request, no fallback. The disclosure depends on the host: loopback stays on this
/// computer; anything else (LAN, <c>.local</c>, private IPs) is disclosed as leaving it, with an extra warning for plain http.
/// Redirects are not followed, so a server cannot forward the transcript to another host.</summary>
public sealed record OpenAICompatibleCleanupProvider : ICleanupProvider
{
    /// <summary>Documented default base URL (see PROVIDERS.md).</summary>
    public static readonly string OllamaDefault = ProvidersData.Value.OpenAI.OllamaDefault;
    /// <summary>Local models can take a long time on a cold load; bounded so a hung server still fails.</summary>
    public static readonly double DefaultTimeout = ProvidersData.Value.OpenAI.DefaultTimeout;
    public static readonly double MaximumTimeout = ProvidersData.Value.OpenAI.MaximumTimeout;

    public ProviderID Id { get; init; }
    /// <summary>Normalized base without a trailing <c>/v1</c>.</summary>
    public ProviderUrl BaseURL { get; init; }
    public string Model { get; init; }
    /// <summary>Optional; sent as <c>Authorization: Bearer</c> only when set and allowed (<see cref="SendableKey"/>).</summary>
    public APIKey? ApiKey { get; init; }
    /// <summary>Seconds; clamped to 1...<see cref="MaximumTimeout"/> when used.</summary>
    public double Timeout { get; init; } = DefaultTimeout;
    public int? MaxTokens { get; init; } = ProviderLimits.MaxOutputTokens;
    /// <summary>Sent as <c>reasoning_effort</c> with cleanup requests; null sends none. Meeting notes leave it to the provider's default.</summary>
    public string? ReasoningEffort { get; init; }
    public IHttpTransport Transport { get; init; } = HttpClientTransport.Default;

    /// <summary>Throws invalidConfiguration(localServer) for a URL <see cref="LocalEndpoint.Normalize"/> rejects.</summary>
    public OpenAICompatibleCleanupProvider(string baseURL, string model, APIKey? apiKey = null, ProviderID provider = ProviderID.LocalServer)
    {
        BaseURL = LocalEndpoint.Normalize(baseURL);
        Model = model;
        ApiKey = apiKey;
        Id = provider;
    }

    public EndpointLocality Locality => LocalEndpoint.Locality(BaseURL);
    /// <summary>The key is never sent in cleartext: a server off this computer gets it only over https.</summary>
    public APIKey? SendableKey => LocalEndpoint.Sendable(ApiKey, BaseURL);
    public string Address => LocalEndpoint.Address(BaseURL);
    public ProviderUrl ChatCompletionsURL => BaseURL.Appending("/v1/chat/completions");
    public ProviderUrl ModelsURL => BaseURL.Appending("/v1/models");
    /// <summary>Ollama's native model list, used only when <c>/v1/models</c> is missing (404).</summary>
    public ProviderUrl OllamaTagsURL => BaseURL.Appending("/api/tags");
    public string? ModelID => Model;
    public CleanupShape Shape => new();
    public ICleanupProvider ForMeetingNotes() => this with { Timeout = MaximumTimeout, ReasoningEffort = null };
    public double EffectiveTimeout => Math.Min(Math.Max(Timeout, 1), MaximumTimeout);

    public DataDisclosure Disclosure
    {
        get
        {
            var locality = Locality;
            if (locality.IsOnDevice)
                return new DataDisclosure(Id, ProcessingStage.Cleanup, Address, [], ["audio", "the transcript text", "the dictionary", "history"],
                    "The transcript and relevant dictionary terms go only to the model server on this Mac; that server's own logging settings apply.");
            return new DataDisclosure(Id, ProcessingStage.Cleanup, locality.Host!, ["the transcript text", "relevant dictionary terms"],
                ["audio", "history", "the rest of the dictionary"], "Retention is up to whoever runs that server.",
                locality.Encrypted ? null : "The connection uses plain http and is not encrypted: anyone on the network path can read the transcript."
                    + (ApiKey is null ? "" : " Your API key is not sent over plain http; use https to send it."));
        }
    }

    /// <summary>Stands for Swift's default argument <c>maxTokens: ProviderLimits.maxOutputTokens</c>, which C# cannot write as a default.</summary>
    public const int DefaultMaxTokens = -1;

    public static ProviderRequest MakeRequest(CleanupMessages messages, ProviderUrl endpoint, string model, APIKey? apiKey, int? maxTokens = DefaultMaxTokens,
                                              string? reasoningEffort = null, ProviderID provider = ProviderID.LocalServer)
    {
        RequireLocalModel(model, provider);
        return OpenAIChatFormat.MakeRequest(endpoint, apiKey, model, messages, maxTokens == DefaultMaxTokens ? ProviderLimits.MaxOutputTokens : maxTokens, reasoningEffort);
    }

    public static string Parse(HttpResult result, ProviderID provider = ProviderID.LocalServer) => OpenAIChatFormat.Parse(provider, result);

    /// <summary>Model ids may contain characters such as <c>@</c> or <c>:</c> (LM Studio, Ollama tags). They are only
    /// sent inside the JSON body, so any printable ASCII up to 200 characters is accepted.</summary>
    public static void RequireLocalModel(string model, ProviderID provider = ProviderID.LocalServer)
    {
        if (model.Length == 0 || SwiftText.Count(model) > 200 || !model.All(c => c > 0x20 && c < 0x7F))
            throw new ProviderException(ProviderError.InvalidConfiguration(provider));
    }

    public ProviderRequest MakeGetRequest(ProviderUrl url)
    {
        var request = new ProviderRequest { Method = "GET", Url = url };
        if (SendableKey is APIKey key) request = request.WithHeader("Authorization", $"Bearer {key.Secret}");
        return request.WithHeader("Accept", "application/json");
    }

    /// <summary>OpenAI list shape <c>{"object":"list","data":[{"id":"..."}]}</c> (Ollama, LM Studio, llama-server, vLLM).
    /// llama-server may also include a <c>models</c> array; <c>data</c> is used.</summary>
    public static IReadOnlyList<string> ParseModelList(HttpResult result)
    {
        if (result.Status is < 200 or >= 300) throw new ProviderException(ProviderErrorClassifier.Classify(ProviderID.LocalServer, result));
        if (Json.Object(result.Body) is not JsonElement obj || Json.Objects(obj, "data") is not { } data)
            throw new ProviderException(ProviderError.MalformedResponse(ProviderID.LocalServer));
        return UniqueSorted(data.Select(d => Json.String(d, "id")));
    }

    /// <summary>Ollama native <c>{"models":[{"name":"llama3.2:latest","model":"llama3.2:latest",...}]}</c>.</summary>
    public static IReadOnlyList<string> ParseOllamaTags(HttpResult result)
    {
        if (result.Status is < 200 or >= 300) throw new ProviderException(ProviderErrorClassifier.Classify(ProviderID.LocalServer, result));
        if (Json.Object(result.Body) is not JsonElement obj || Json.Objects(obj, "models") is not { } models)
            throw new ProviderException(ProviderError.MalformedResponse(ProviderID.LocalServer));
        return UniqueSorted(models.Select(m => Json.String(m, "model") ?? Json.String(m, "name")));
    }

    static List<string> UniqueSorted(IEnumerable<string?> ids)
    {
        static bool Valid(string id) { try { RequireLocalModel(id); return true; } catch (ProviderException) { return false; } }
        // Swift sorts Strings by Unicode scalar values; the ids are printable ASCII, so ordinal order is the same.
        return [.. ids.OfType<string>().Where(Valid).Distinct().Order(StringComparer.Ordinal)];
    }

    public async Task<string> CleanupAsync(CleanupMessages messages, CancellationToken cancellationToken = default)
    {
        var request = MakeRequest(messages, ChatCompletionsURL, Model, SendableKey, MaxTokens, ReasoningEffort, Id);
        return Parse(await new ProviderHTTP(Id, Transport, EffectiveTimeout).ExecuteAsync(request, cancellationToken).ConfigureAwait(false), Id);
    }

    /// <summary>Lists model ids. Tries <c>GET {base}/v1/models</c>; only if that returns 404, tries Ollama's
    /// <c>GET {base}/api/tags</c> on the same server. Sends no transcript.</summary>
    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        var http = new ProviderHTTP(Id, Transport, ProviderLimits.ValidationDeadline);
        var first = await http.ExecuteAsync(MakeGetRequest(ModelsURL), cancellationToken).ConfigureAwait(false);
        if (first.Status == 404) return ParseOllamaTags(await http.ExecuteAsync(MakeGetRequest(OllamaTagsURL), cancellationToken).ConfigureAwait(false));
        return ParseModelList(first);
    }

    /// <summary>Confirms a server answers <c>GET /v1/models</c> (and accepts the key, if one is set).</summary>
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        var result = await new ProviderHTTP(Id, Transport, ProviderLimits.ValidationDeadline).ExecuteAsync(MakeGetRequest(ModelsURL), cancellationToken).ConfigureAwait(false);
        ProviderHTTP.ValidationResult(Id, result);
    }
}
