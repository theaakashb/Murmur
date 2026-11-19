// Loopback-only OpenAI-compatible fake for the exit harness.
//   FakeProvider --manifest <manifest.json> [--port N] [--port-file <path>]   serve until killed
//   FakeProvider --selftest                                                   assert-based self-test
// The request log is content-free: field names, byte counts and model names, never audio or text.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Contains("--selftest")) return await SelfTest.Run();

string Arg(string name, string? fallback = null) =>
    args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback ?? throw new ArgumentException($"missing {name}");

var manifest = Server.LoadManifest(Arg("--manifest"));
var app = Server.Build(manifest, int.Parse(Arg("--port", "0")));
await app.StartAsync();
var url = app.Urls.First();
Console.WriteLine($"LISTENING {url}");
if (args.Contains("--port-file")) File.WriteAllText(Arg("--port-file"), url);
await app.WaitForShutdownAsync();
return 0;

static class Server
{
public static Dictionary<string, (string Spoken, string Cleanup)> LoadManifest(string path) =>
    JsonNode.Parse(File.ReadAllText(path))!.AsArray()
        .ToDictionary(n => (string)n!["id"]!, n => ((string)n!["spoken"]!, (string)n!["cleanup"]!));

public static WebApplication Build(Dictionary<string, (string Spoken, string Cleanup)> manifest, int port)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port)); // 127.0.0.1 only
    var app = builder.Build();
    string? current = null;
    var log = new List<object>();
    var gate = new object();
    void Record(object entry) { lock (gate) log.Add(entry); }

    // Defense in depth: bound to loopback already, but refuse anything whose peer is not.
    app.Use(async (ctx, next) =>
    {
        if (ctx.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) { ctx.Response.StatusCode = 403; return; }
        await next();
    });

    app.MapPost("/control/next", async (HttpRequest req) =>
    {
        var id = (string?)(await JsonNode.ParseAsync(req.Body))?["id"];
        if (id is null || !manifest.ContainsKey(id)) return Results.NotFound(new { error = "unknown id" });
        lock (gate) current = id;
        return Results.Ok(new { id });
    });
    app.MapGet("/control/requests", () => { lock (gate) return Results.Json(log.ToArray()); });
    app.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = new[] { new { id = "fake-model", @object = "model" } } }));

    app.MapPost("/v1/audio/transcriptions", async (HttpRequest req) =>
    {
        if (!req.HasFormContentType) return Results.BadRequest(new { error = "multipart required" });
        var form = await req.ReadFormAsync();
        var fields = form.Keys.Concat(form.Files.Select(f => f.Name)).ToArray();
        var audio = form.Files.FirstOrDefault();
        string? id; lock (gate) id = current;
        Record(new { path = "/v1/audio/transcriptions", fields, audioBytes = audio?.Length ?? 0, fixture = id });
        if (id is null) return Results.BadRequest(new { error = "no fixture selected" });
        return Results.Json(new { text = manifest[id].Spoken });
    });

    app.MapPost("/v1/chat/completions", async (HttpRequest req) =>
    {
        var body = await JsonNode.ParseAsync(req.Body);
        string? id; lock (gate) id = current;
        Record(new { path = "/v1/chat/completions", model = (string?)body?["model"], messages = body?["messages"]?.AsArray().Count ?? 0, fixture = id });
        if (id is null) return Results.BadRequest(new { error = "no fixture selected" });
        return Results.Json(new
        {
            id = "chatcmpl-fake", @object = "chat.completion", model = (string?)body?["model"] ?? "fake-model",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = manifest[id].Cleanup }, finish_reason = "stop" } },
        });
    });
    return app;
}
}

static class SelfTest
{
    public static async Task<int> Run()
    {
        var m = new Dictionary<string, (string, string)> { ["a"] = ("hello there", "Hello there."), ["b"] = ("two", "Two.") };
        var dir = Path.Combine(Path.GetTempPath(), "fp-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        var mf = Path.Combine(dir, "m.json");
        File.WriteAllText(mf, JsonSerializer.Serialize(m.Select(kv => new { id = kv.Key, spoken = kv.Value.Item1, cleanup = kv.Value.Item2 })));
        var app = Server.Build(Server.LoadManifest(mf), 0);
        await app.StartAsync();
        var url = app.Urls.First();
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fail++; }

        Check(url.StartsWith("http://127.0.0.1:"), "binds 127.0.0.1 only (" + url + ")");
        Check((await http.GetAsync("/v1/models")).IsSuccessStatusCode, "GET /v1/models");
        Check((await http.PostAsync("/v1/chat/completions", new StringContent("{}"))).StatusCode == HttpStatusCode.BadRequest, "no fixture selected -> 400");
        Check((await http.PostAsync("/control/next", new StringContent("{\"id\":\"zzz\"}"))).StatusCode == HttpStatusCode.NotFound, "unknown id -> 404");
        Check((await http.PostAsync("/control/next", new StringContent("{\"id\":\"a\"}"))).IsSuccessStatusCode, "control/next a");

        using var mp = new MultipartFormDataContent { { new StringContent("whisper-x"), "model" }, { new StringContent("json"), "response_format" } };
        mp.Add(new ByteArrayContent(new byte[1234]), "file", "audio.wav");
        var tr = JsonNode.Parse(await (await http.PostAsync("/v1/audio/transcriptions", mp)).Content.ReadAsStringAsync())!;
        Check((string?)tr["text"] == "hello there", "transcription returns fixture transcript");

        var chat = JsonNode.Parse(await (await http.PostAsync("/v1/chat/completions", new StringContent("{\"model\":\"m1\",\"messages\":[{\"role\":\"user\",\"content\":\"SECRET\"}]}", System.Text.Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync())!;
        Check((string?)chat["choices"]![0]!["message"]!["content"] == "Hello there." && (string?)chat["choices"]![0]!["finish_reason"] == "stop", "chat returns cleanup");

        await http.PostAsync("/control/next", new StringContent("{\"id\":\"b\"}"));
        var tr2 = JsonNode.Parse(await (await http.PostAsync("/v1/audio/transcriptions", mp)).Content.ReadAsStringAsync());
        Check((string?)tr2?["text"] == "two", "control/next switches fixture");

        var logText = await http.GetStringAsync("/control/requests");
        var log = JsonNode.Parse(logText)!.AsArray();
        var first = log.First(e => (string?)e!["path"] == "/v1/audio/transcriptions")!;
        Check(log.Count == 4 && (int)first["audioBytes"]! == 1234, "request log records audio byte length");
        Check(first["fields"]!.AsArray().Select(x => (string)x!).OrderBy(x => x).SequenceEqual(["file", "model", "response_format"]), "request log records multipart field names");
        Check(!logText.Contains("SECRET") && !logText.Contains("hello there"), "request log is content-free");

        await app.StopAsync();
        Directory.Delete(dir, true);
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");
        return fail == 0 ? 0 : 1;
    }
}
