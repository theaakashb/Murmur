// Stub "Murmur.exe" for harness self-tests only. Polls Right Ctrl, then: transcribe + clean through the settings.json provider,
// stage the text on the clipboard with the exclusion formats, send Ctrl+V, write History, restore the clipboard, emit events.
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Runtime.InteropServices;

namespace Murmur.Harness.Driver;

static class StubApp
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    static string data = "", events = "";
    static void Emit(string name, object? extra = null)
    {
        var o = new JsonObject { ["t"] = DateTime.UtcNow.ToString("O"), ["event"] = name };
        if (extra is not null) foreach (var p in JsonSerializer.SerializeToNode(extra)!.AsObject()) o[p.Key] = p.Value?.DeepClone();
        File.AppendAllText(events, o.ToJsonString() + "\n");
    }

    static void Main()
    {
        data = Environment.GetEnvironmentVariable("MURMUR_DATA_DIR")!;
        var synth = Environment.GetEnvironmentVariable("MURMUR_SYNTHETIC_AUDIO")!;
        events = Path.Combine(data, "diagnostics", "events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(events)!); Directory.CreateDirectory(Path.Combine(data, "history"));
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "settings.json")))!;
        using var http = new HttpClient { BaseAddress = new Uri(((string)s["customAPIBaseURL"]!).TrimEnd('/') + "/") };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => File.WriteAllText(Path.Combine(data, "stub-crash.txt"), e.ExceptionObject.ToString());
        Emit("ready"); Emit("hook-installed");
        while (true)
        {
            while ((GetAsyncKeyState(Native.VK_RCONTROL) & 0x8000) == 0) Thread.Sleep(10);
            var t0 = DateTime.UtcNow; Emit("recording-started");
            while ((GetAsyncKeyState(Native.VK_RCONTROL) & 0x8000) != 0) Thread.Sleep(10);
            Emit("recording-stopped", new { seconds = (DateTime.UtcNow - t0).TotalSeconds });
            try
            {
                var wav = File.ReadAllBytes(File.ReadAllText(Path.Combine(synth, "next.txt")).Trim());
                using var mp = new MultipartFormDataContent { { new StringContent("fake-asr"), "model" }, { new ByteArrayContent(wav), "file", "audio.wav" } };
                var raw = (string)JsonNode.Parse(http.PostAsync("audio/transcriptions", mp).Result.Content.ReadAsStringAsync().Result)!["text"]!;
                Emit("transcribed");
                var body = new StringContent("{\"model\":\"fake-cleanup\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", Encoding.UTF8, "application/json");
                var final = (string)JsonNode.Parse(http.PostAsync("chat/completions", body).Result.Content.ReadAsStringAsync().Result)!["choices"]![0]!["message"]!["content"]!;
                Emit("cleaned", new { path = "custom" });

                var before = Native.GetClipboardText();
                Native.SetClipboard(final, true); Thread.Sleep(50);
                Native.Chord(Native.VK_CONTROL, 'V'); Thread.Sleep(150);
                var id = Guid.NewGuid().ToString();
                File.WriteAllText(Path.Combine(data, "history", id + ".json"), new JsonObject { ["id"] = id, ["rawText"] = raw, ["finalText"] = final, ["insertionStatus"] = "verified" }.ToJsonString());
                Emit("paste", new { status = "verified", historyId = id });
                Thread.Sleep(500);
                if (before is not null) Native.SetClipboard(before, false);
                Emit("clipboard-restored");
            }
            catch (Exception e) { Emit("error", new { stage = "stub", reason = e.GetType().Name }); }
        }
    }
}
