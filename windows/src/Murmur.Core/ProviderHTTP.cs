// Port of app/ProviderHTTP.swift.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Murmur.Core;

// HTTP transport, deadline, error classification and multipart bodies shared by every provider.

/// <summary>One HTTP request as the providers build it (Swift's URLRequest). <see cref="ToString"/> never shows headers or the body,
/// so the Authorization header and transcript text cannot reach a log through it.</summary>
public sealed record ProviderRequest
{
    public required string Method { get; init; }
    public required ProviderUrl Url { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
    public byte[]? Body { get; init; }
    /// <summary>Seconds; <see cref="ProviderHTTP"/> sets it to its deadline. URLRequest's default is 60.</summary>
    public double TimeoutInterval { get; init; } = 60;

    /// <summary>Case-insensitive header lookup (URLRequest.value(forHTTPHeaderField:)).</summary>
    public string? Header(string name) => Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Sets a header, replacing any with the same name (URLRequest.setValue).</summary>
    public ProviderRequest WithHeader(string name, string value) =>
        this with { Headers = [.. Headers.Where(h => !string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)), new(name, value)] };

    public override string ToString() => $"{Method} {Url} ({Body?.Length ?? 0} bytes)";
}

/// <summary>A response. <see cref="ToString"/> never shows the body.</summary>
public sealed record HttpResult(int Status, IReadOnlyDictionary<string, string>? Headers = null, byte[]? Body = null)
{
    public IReadOnlyDictionary<string, string> Headers { get; init; } = Headers ?? new Dictionary<string, string>();
    public byte[] Body { get; init; } = Body ?? [];

    /// <summary>Case-insensitive header lookup.</summary>
    public string? Header(string name) => Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public override string ToString() => $"HTTP {Status} ({Body.Length} bytes)";
}

public interface IHttpTransport
{
    /// <summary>Throws <see cref="TransportException"/> (content-free) on failure; <see cref="TransportError.Cancelled"/> when the token is cancelled.</summary>
    Task<HttpResult> SendAsync(ProviderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The URLError codes the providers distinguish.</summary>
public enum TransportError
{
    Cancelled, TimedOut, DataLengthExceedsMaximum, CannotConnectToHost, NotConnectedToInternet, CannotFindHost,
    NetworkConnectionLost, SecureConnectionFailed, BadServerResponse, Unknown,
}

/// <summary>A transport failure. The message is only the code: never a URL, header, body or inner exception text.</summary>
public sealed class TransportException(TransportError code) : Exception($"Transport failure: {code}.")
{
    public TransportError Code { get; } = code;
}

/// <summary>Sensitive requests refuse redirects, cookies, credentials, cached data and oversized responses.
/// A 3xx is returned as the response, so a server can never forward a key or transcript to another host.</summary>
public sealed class HttpClientTransport : IHttpTransport, IDisposable
{
    /// <summary>One shared client for providers that are not given a transport.</summary>
    public static readonly HttpClientTransport Default = new();

    readonly HttpClient client;
    readonly int maximumResponseBytes;

    public HttpClientTransport(int maximumResponseBytes = 8 * 1024 * 1024)
    {
        this.maximumResponseBytes = Math.Max(1, maximumResponseBytes);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => client.Dispose();

    public async Task<HttpResult> SendAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) throw new TransportException(TransportError.Cancelled);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutInterval, 0.001, int.MaxValue / 1000.0)));
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(request.Url.ToString()));
        if (request.Body is not null) message.Content = new ByteArrayContent(request.Body);
        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else message.Headers.TryAddWithoutValidation(name, value);
        }
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long declared && declared > maximumResponseBytes)
                throw new TransportException(TransportError.DataLengthExceedsMaximum);
            var body = await ReadCapped(response.Content, timer.Token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers)) headers[name] = string.Join(", ", values);
            return new HttpResult((int)response.StatusCode, headers, body);
        }
        catch (TransportException) { throw; }
        catch (OperationCanceledException)
        {
            throw new TransportException(cancellationToken.IsCancellationRequested ? TransportError.Cancelled : TransportError.TimedOut);
        }
        catch (HttpRequestException e)
        {
            if (cancellationToken.IsCancellationRequested) throw new TransportException(TransportError.Cancelled);
            throw new TransportException(Code(e));
        }
        catch (IOException)
        {
            if (cancellationToken.IsCancellationRequested) throw new TransportException(TransportError.Cancelled);
            throw new TransportException(TransportError.NetworkConnectionLost);
        }
    }

    async Task<byte[]> ReadCapped(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (read > maximumResponseBytes - buffer.Length) throw new TransportException(TransportError.DataLengthExceedsMaximum);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    static TransportError Code(HttpRequestException e)
    {
        var socket = e.InnerException as SocketException ?? e.InnerException?.InnerException as SocketException;
        switch (socket?.SocketErrorCode)
        {
            case SocketError.ConnectionRefused: return TransportError.CannotConnectToHost;
            case SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain: return TransportError.CannotFindHost;
            case SocketError.NetworkDown or SocketError.NetworkUnreachable: return TransportError.NotConnectedToInternet;
            case SocketError.TimedOut: return TransportError.TimedOut;
        }
        return e.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => TransportError.CannotFindHost,
            HttpRequestError.SecureConnectionError => TransportError.SecureConnectionFailed,
            HttpRequestError.ResponseEnded => TransportError.NetworkConnectionLost,
            HttpRequestError.InvalidResponse => TransportError.BadServerResponse,
            HttpRequestError.ConfigurationLimitExceeded => TransportError.DataLengthExceedsMaximum,
            _ => TransportError.Unknown,
        };
    }
}

/// <summary>Executes one request with a total deadline and maps transport failures to <see cref="ProviderError"/>.</summary>
public sealed record ProviderHTTP(ProviderID Provider, IHttpTransport Transport, double Deadline)
{
    public async Task<HttpResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) throw new ProviderException(ProviderError.Cancelled);
        var timed = request with { TimeoutInterval = Deadline };
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Raced against a timer, as Swift's task group does, so a transport that ignores cancellation still times out.
        var send = Task.Run(() => Transport.SendAsync(timed, race.Token), CancellationToken.None);
        var timer = Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Deadline, 0.001, int.MaxValue / 1000.0)), race.Token);
        var first = await Task.WhenAny(send, timer).ConfigureAwait(false);
        race.Cancel();
        if (first != send)
        {
            _ = send.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (cancellationToken.IsCancellationRequested) throw new ProviderException(ProviderError.Cancelled);
            throw new ProviderException(ProviderError.Timeout(ProviderErrorClassifier.Reported(Provider, timed.Url)));
        }
        HttpResult result;
        try { result = await send.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (cancellationToken.IsCancellationRequested) throw new ProviderException(ProviderError.Cancelled);
            if (error is ProviderException) throw;
            throw new ProviderException(ProviderErrorClassifier.Classify(Provider, error, timed.Url));
        }
        if (cancellationToken.IsCancellationRequested) throw new ProviderException(ProviderError.Cancelled);
        return result;
    }

    /// <summary>Swift's top-level <c>validationResult</c>: a 2xx passes, anything else is classified and thrown.</summary>
    public static void ValidationResult(ProviderID provider, HttpResult result)
    {
        if (result.Status is < 200 or >= 300) throw new ProviderException(ProviderErrorClassifier.Classify(provider, result));
    }
}

public static class ProviderErrorClassifier
{
    /// <summary><paramref name="url"/> names the address of a local server that refused the connection; any provider aimed at a
    /// loopback address (Ollama, LM Studio) gets that error, not only the legacy local-server id.</summary>
    public static ProviderError Classify(ProviderID provider, Exception error, ProviderUrl? url = null)
    {
        if (error is OperationCanceledException) return ProviderError.Cancelled;
        var reported = Reported(provider, url);
        if (error is TransportException transport)
        {
            switch (transport.Code)
            {
                case TransportError.Cancelled: return ProviderError.Cancelled;
                case TransportError.TimedOut: return ProviderError.Timeout(reported);
                case TransportError.DataLengthExceedsMaximum: return ProviderError.MalformedResponse(provider);
                case TransportError.CannotConnectToHost when provider == ProviderID.LocalServer || (url is not null && LocalEndpoint.Locality(url).IsOnDevice):
                    return ProviderError.ServerNotRunning(provider, url is null ? "the configured address" : LocalEndpoint.Address(url));
                default: return ProviderError.Network(reported);
            }
        }
        return ProviderError.Network(reported);
    }

    /// <summary>A server on this computer that cannot be reached or is slow is not an internet problem: a network or
    /// timeout error for a loopback address is reported as the local model server's, whichever provider was aimed at it.</summary>
    public static ProviderID Reported(ProviderID provider, ProviderUrl? url) =>
        url is not null && LocalEndpoint.Locality(url).IsOnDevice ? ProviderID.LocalServer : provider;

    /// <summary>Classifies a non-success HTTP result, or a 200 that carries an error object (some gateways
    /// report errors raised mid-generation that way). <paramref name="now"/> null is the current time.</summary>
    public static ProviderError Classify(ProviderID provider, HttpResult result, DateTimeOffset? now = null)
    {
        var status = result.Status;
        if (status is >= 200 and < 300)
        {
            if (ErrorBody.NumericCode(result.Body) is not long code || code < 400) return ProviderError.Server(provider, null);
            if (code > int.MaxValue) return ProviderError.Server(provider, null);
            status = (int)code;
        }
        var retryAfter = ParseRetryAfter(result.Header("Retry-After"), now ?? DateTimeOffset.UtcNow);
        return status switch
        {
            401 or 403 => ProviderError.Authentication(provider),
            402 => ProviderError.Quota(provider),
            413 => ProviderError.PayloadTooLarge(provider, null),
            408 or 504 => ProviderError.Timeout(provider),
            // Redirects are not followed; any 3xx here is unusable.
            >= 300 and < 400 => ProviderError.Rejected(provider, status),
            429 => ProviderError.RateLimited(provider, retryAfter),
            >= 400 and < 500 => ProviderError.Rejected(provider, status),
            _ => ProviderError.Server(provider, status),
        };
    }

    static readonly Regex Decimal = new(@"^[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex Hex = new(@"^([+-]?)0[xX]([0-9a-fA-F]*)(?:\.([0-9a-fA-F]*))?(?:[pP]([+-]?[0-9]+))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex HttpDate = new(@"^([A-Za-z]{3}), ([0-9]{1,2}) ([A-Za-z]{3}) ([0-9]{4}) ([0-9]{1,2}):([0-9]{1,2}):([0-9]{1,2}) (GMT|UTC)$",
                                         RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    static readonly string[] Weekdays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary><c>Retry-After</c> as delta-seconds or an HTTP-date, in seconds; null when absent, unparsable, negative or not finite.</summary>
    public static double? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        var raw = value?.Trim(' ', '\t');
        if (string.IsNullOrEmpty(raw)) return null;
        if (SwiftDouble(raw) is double seconds) return double.IsFinite(seconds) && seconds >= 0 && seconds < long.MaxValue ? seconds : null;
        var m = HttpDate.Match(raw);
        if (!m.Success || !Weekdays.Contains(m.Groups[1].Value.ToLowerInvariant())) return null;
        var month = Array.IndexOf(Months, m.Groups[3].Value.ToLowerInvariant()) + 1;
        int Part(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        if (month == 0) return null;
        try
        {
            var date = new DateTimeOffset(Part(4), month, Part(2), Part(5), Part(6), Part(7), TimeSpan.Zero);
            return Math.Max(0, (date - now).TotalSeconds);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Swift's <c>Double(String)</c> for finite decimal and hexadecimal forms; null otherwise ("inf" and "nan" are
    /// rejected by the caller anyway, so they are treated as unparsable).</summary>
    static double? SwiftDouble(string s)
    {
        if (Decimal.IsMatch(s)) return double.Parse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
        var h = Hex.Match(s);
        if (!h.Success || h.Groups[2].Length + h.Groups[3].Length == 0) return null;
        double v = 0;
        foreach (var c in h.Groups[2].Value) v = v * 16 + Convert.ToInt32(c.ToString(), 16);
        var scale = 1.0 / 16;
        foreach (var c in h.Groups[3].Value) { v += Convert.ToInt32(c.ToString(), 16) * scale; scale /= 16; }
        if (h.Groups[4].Success) v *= Math.Pow(2, int.Parse(h.Groups[4].Value, CultureInfo.InvariantCulture));
        return h.Groups[1].Value == "-" ? -v : v;
    }
}

/// <summary>The numeric <c>error.code</c> of an error body, for a 2xx response that carries an error.</summary>
public static class ErrorBody
{
    public static long? NumericCode(byte[] data)
    {
        if (Json.Object(data) is not JsonElement root || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("code", out var code)) return null;
        return Json.SwiftInt(code) ?? (code.ValueKind == JsonValueKind.String ? Json.SwiftIntString(code.GetString()!) : null);
    }
}

/// <summary>JSONSerialization's view of a body, as the Swift parsers cast it.</summary>
internal static class Json
{
    /// <summary>The top-level object, or null when the body is not JSON or not an object.</summary>
    public static JsonElement? Object(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary><c>value as? Int</c> on an NSNumber: an exactly integral number. Booleans bridge to 0 and 1.</summary>
    public static long? SwiftInt(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var n)) return n;
                if (value.TryGetDouble(out var d) && Math.Floor(d) == d && d >= long.MinValue && d < long.MaxValue) return (long)d;
                return null;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            default: return null;
        }
    }

    /// <summary><c>value as? Double</c> on an NSNumber (booleans bridge to 0 and 1).</summary>
    public static double? SwiftDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    /// <summary>Swift's <c>Int(String)</c>: an optional sign and ASCII digits, nothing else.</summary>
    public static long? SwiftIntString(string s) =>
        s.Length > 0 && s.Skip(s[0] is '+' or '-' ? 1 : 0).All(char.IsAsciiDigit) && s.Length > (s[0] is '+' or '-' ? 1 : 0)
            && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary><c>value as? [[String: Any]]</c>: an array whose elements are all objects.</summary>
    public static List<JsonElement>? Objects(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var items = v.EnumerateArray().ToList();
        return items.All(i => i.ValueKind == JsonValueKind.Object) ? items : null;
    }
}

public static class Multipart
{
    public static string MakeBoundary() => "MurmurBoundary-" + Guid.NewGuid().ToString("D").ToUpperInvariant();

    /// <summary>multipart/form-data body with text fields followed by one file part.</summary>
    public static byte[] Body(string boundary, IReadOnlyList<(string Name, string Value)> fields, string fileField, string filename, string mimeType, byte[] fileData)
    {
        using var body = new MemoryStream();
        void Append(string s) { var bytes = Encoding.UTF8.GetBytes(s); body.Write(bytes); }
        foreach (var (name, value) in fields)
        {
            Append($"--{boundary}\r\n");
            Append($"Content-Disposition: form-data; name=\"{Sanitize(name)}\"\r\n\r\n");
            Append(value);
            Append("\r\n");
        }
        Append($"--{boundary}\r\n");
        Append($"Content-Disposition: form-data; name=\"{Sanitize(fileField)}\"; filename=\"{Sanitize(filename)}\"\r\n");
        Append($"Content-Type: {Sanitize(mimeType)}\r\n\r\n");
        body.Write(fileData);
        Append($"\r\n--{boundary}--\r\n");
        return body.ToArray();
    }

    /// <summary>Prevents header injection through names, filenames or MIME types.</summary>
    public static string Sanitize(string value) => string.Concat(value.Where(c => c is not ('"' or '\r' or '\n' or '\\')));
}
