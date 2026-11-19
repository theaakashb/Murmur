// Ports app/check_http.swift and app/check_http.py: the real HttpClient transport against an in-process loopback server.
// Synthetic data only, no credentials, no external network.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>A tiny HTTP/1.1 server on 127.0.0.1 reproducing check_http.py's endpoints. Every response closes the connection.</summary>
internal sealed class LoopbackServer : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly Lock gate = new();
    readonly List<string> received = [];
    public int Port { get; }
    public string Base => $"http://127.0.0.1:{Port}";
    /// <summary>Requests that reached /sink, the redirect target: must stay empty.</summary>
    public string[] Received { get { lock (gate) return [.. received]; } }

    public LoopbackServer()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }

    async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var head = new List<byte>();
            var one = new byte[1];
            while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await stream.ReadAsync(one) == 0) return;
                head.Add(one[0]);
            }
            var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n");
            var parts = lines[0].Split(' ');
            var (method, path) = (parts[0], parts[1]);
            var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
            var length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
            var body = new byte[length];
            for (var read = 0; read < length;) { var n = await stream.ReadAsync(body.AsMemory(read)); if (n == 0) return; read += n; }

            async Task Respond(int status, byte[] content, bool declareLength = true, string? location = null)
            {
                var text = new StringBuilder($"HTTP/1.1 {status} X\r\nConnection: close\r\n");
                if (location is not null) text.Append($"Location: {location}\r\n");
                if (declareLength) text.Append($"Content-Length: {content.Length}\r\n");
                text.Append("\r\n");
                try
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()));
                    await stream.WriteAsync(content);
                }
                catch (IOException) { } // the client gave up on an oversized or cancelled response
            }

            if (path == "/sink")
            {
                lock (gate) received.Add($"{method} {path} key={(headers.ContainsKey("x-goog-api-key") || headers.ContainsKey("authorization") ? "yes" : "no")}");
                await Respond(200, []);
            }
            else if (method == "POST" && path.StartsWith("/redirect/")) await Respond(int.Parse(path[10..]), [], location: $"http://localhost:{Port}/sink");
            else if (path == "/slow") { await Task.Delay(200); await Respond(200, "ok"u8.ToArray()); }
            else if (path is "/large" or "/streamed") await Respond(200, Enumerable.Repeat((byte)'x', 2048).ToArray(), declareLength: path == "/large");
            else if (path.StartsWith("/exact/")) await Respond(200, Enumerable.Repeat((byte)'x', int.Parse(path[7..])).ToArray());
            else if (path.StartsWith("/stream/")) await Respond(200, Enumerable.Repeat((byte)'x', int.Parse(path[8..])).ToArray(), declareLength: false);
            else if (path.StartsWith("/status/")) await Respond(int.Parse(path[8..]), "{}"u8.ToArray());
            else await Respond(200, "ok"u8.ToArray());
        }
        catch (IOException) { }
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

public class ProviderHttpTests : IDisposable
{
    readonly LoopbackServer server = new();
    readonly HttpClientTransport transport = new(maximumResponseBytes: 1024);
    public void Dispose() { transport.Dispose(); server.Dispose(); }

    ProviderRequest Get(string path) => new() { Method = "GET", Url = ProviderUrl.Parse(server.Base + path), TimeoutInterval = 10 };

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task RedirectsAreRefusedAndKeysNeverForwarded(int status)
    {
        var request = new ProviderRequest { Method = "POST", Url = ProviderUrl.Parse($"{server.Base}/redirect/{status}"), Body = "synthetic-transcript"u8.ToArray(), TimeoutInterval = 10 }
            .WithHeader("x-goog-api-key", "synthetic-key").WithHeader("Authorization", "Bearer synthetic-key");
        var response = await transport.SendAsync(request);
        Assert.Equal(status, response.Status);
        Assert.Empty(server.Received);
    }

    [Theory]
    [InlineData("/large")]
    [InlineData("/streamed")]
    [InlineData("/exact/1025")]
    [InlineData("/stream/1025")]
    public async Task OversizedResponsesAreRejected(string path)
    {
        var e = await Assert.ThrowsAsync<TransportException>(() => transport.SendAsync(Get(path)));
        Assert.Equal(TransportError.DataLengthExceedsMaximum, e.Code);
    }

    [Theory]
    [InlineData("/exact/1024")]
    [InlineData("/stream/1024")]
    public async Task ResponsesAtTheCapAreAccepted(string path) => Assert.Equal(1024, (await transport.SendAsync(Get(path))).Body.Length);

    [Fact]
    public async Task NormalResponse()
    {
        var ok = await transport.SendAsync(Get("/ok"));
        Assert.True(ok.Status == 200 && ok.Body.SequenceEqual("ok"u8.ToArray()));
    }

    [Fact]
    public async Task CancellationCancelsTheRequest()
    {
        using var cts = new CancellationTokenSource();
        var task = transport.SendAsync(Get("/slow"), cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        var e = await Assert.ThrowsAsync<TransportException>(() => task);
        Assert.Equal(TransportError.Cancelled, e.Code);
    }

    // Through the providers: the same rules end as content-free ProviderErrors.

    [Fact]
    public async Task ProviderSeesARedirectAsRejectedAndSendsNothingOn()
    {
        // A keyed chat request aimed at the redirecting path (loopback, so the key is sent to it, but never on to the Location).
        var request = OpenAICompatibleCleanupProvider.MakeRequest(new CleanupMessages("s", "synthetic transcript"), ProviderUrl.Parse(server.Base + "/redirect/308"), "m", APIKey.Create("synthetic-key"));
        var result = await new ProviderHTTP(ProviderID.LocalServer, transport, 10).ExecuteAsync(request);
        ProvidersTests.Expect(ProviderError.Rejected(ProviderID.LocalServer, 308), () => OpenAICompatibleCleanupProvider.Parse(result));
        Assert.Equal("Bearer synthetic-key", request.Header("Authorization"));
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ProviderErrorsFromTheRealTransport()
    {
        var provider = new OpenAICompatibleCleanupProvider(server.Base, "m") { Transport = transport };
        var http = new ProviderHTTP(ProviderID.LocalServer, transport, 10);
        var tooBig = await Assert.ThrowsAsync<ProviderException>(() => http.ExecuteAsync(Get("/large")));
        Assert.Equal(ProviderError.MalformedResponse(ProviderID.LocalServer), tooBig.Error);
        var slow = await Assert.ThrowsAsync<ProviderException>(() => new ProviderHTTP(ProviderID.CustomAPI, transport, 0.05).ExecuteAsync(Get("/slow")));
        Assert.Equal(ProviderError.Timeout(ProviderID.LocalServer), slow.Error);
        var status = await http.ExecuteAsync(Get("/status/401"));
        Assert.Equal(ProviderError.Authentication(ProviderID.LocalServer), ProviderErrorClassifier.Classify(ProviderID.LocalServer, status));
        Assert.Equal("ok", Encoding.UTF8.GetString((await http.ExecuteAsync(provider.MakeGetRequest(provider.ModelsURL))).Body));

        // Nothing listening: a free port, closed again.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var closed = new OpenAICompatibleCleanupProvider($"http://127.0.0.1:{port}", "m") { Transport = transport };
        var refused = await Assert.ThrowsAsync<ProviderException>(() => closed.ValidateAsync());
        Assert.Equal(ProviderError.ServerNotRunning(ProviderID.LocalServer, $"127.0.0.1:{port}"), refused.Error);
    }
}
