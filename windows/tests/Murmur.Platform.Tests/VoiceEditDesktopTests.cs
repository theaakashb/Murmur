using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.Platform.Tests;

[Collection("Browser desktop")]
public sealed class VoiceEditDesktopTests
{
    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    static void Key(byte vk, bool up = false) =>
        keybd_event(vk, vk == KeyEvent.VkRControl ? (byte)0x1D : (byte)0,
            (up ? 2u : 0u) | (vk == KeyEvent.VkRControl ? 1u : 0u), 0);

    static async Task WaitFor(Func<bool> check, string message)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (check()) return;
            await Task.Delay(100);
        }
        Assert.Fail(message);
    }

    [VoiceEditDesktopFact]
    public Task SameKeyReplacesSelection() => Run(separate: false, moveSelection: false);

    [VoiceEditDesktopFact]
    public Task SeparateChordOffersCopyWhenSelectionChanges() => Run(separate: true, moveSelection: true);

    static async Task Run(bool separate, bool moveSelection)
    {
        // Opt in only with explicitly built app and fake provider paths.
        var appPath = Environment.GetEnvironmentVariable("MURMUR_VOICE_EDIT_TEST_APP");
        var providerPath = Environment.GetEnvironmentVariable("MURMUR_VOICE_EDIT_TEST_PROVIDER");
        var testDir = Environment.GetEnvironmentVariable("MURMUR_VOICE_EDIT_TEST_ROOT");


        const string original = "old draft", instruction = "make it formal";
        string edited = $"Revised draft {Guid.NewGuid():N}.";
        var root = Directory.CreateDirectory(Path.Combine(testDir!, Guid.NewGuid().ToString("N"))).FullName;
        Process? app = null, provider = null;
        try
        {
            var manifest = Path.Combine(root, "manifest.json");
            var portFile = Path.Combine(root, "port");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new[] { new { id = "edit", spoken = instruction, cleanup = edited } }));
            var fake = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { providerPath!, "--manifest", manifest, "--port-file", portFile }) fake.ArgumentList.Add(arg);
            provider = Process.Start(fake)!;
            await WaitFor(() => File.Exists(portFile) && new FileInfo(portFile).Length > 0, "fake provider did not start");
            var url = File.ReadAllText(portFile).Trim();
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            (await http.PostAsJsonAsync("/control/next", new { id = "edit" })).EnsureSuccessStatusCode();

            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var audio = Directory.CreateDirectory(Path.Combine(root, "audio")).FullName;
            var wav = Path.Combine(audio, "fixture.wav");
            File.WriteAllBytes(wav, WAVAudio.EncodePCM16(Enumerable.Range(0, 16000).Select(i => (float)(0.2 * Math.Sin(i * 0.11))).ToArray(), 16000));
            File.WriteAllText(Path.Combine(audio, "next.txt"), wav);
            File.WriteAllText(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new
            {
                transcriptionRoute = "cloud", cleanupChoice = "custom-api", customAPIBaseURL = url + "/v1",
                customTranscriptionAPIBaseURL = url + "/v1", customAPIModel = "fake-cleanup", customTranscriptionModel = "fake-asr",
                historyMode = "on", shortcut = "right-ctrl", voiceEditShortcut = separate ? "custom:69:1" : "same",
            }));
            var launch = new ProcessStartInfo(appPath!) { UseShellExecute = false };
            launch.Environment["MURMUR_DATA_DIR"] = data;
            launch.Environment["MURMUR_SYNTHETIC_AUDIO"] = audio;
            launch.Environment["MURMUR_TEST_EVENTS"] = "1";
            app = Process.Start(launch)!;
            var events = Path.Combine(data, "diagnostics", "events.jsonl");
            string Events()
            {
                if (!File.Exists(events)) return "";
                using var file = File.Open(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(file);
                return reader.ReadToEnd();
            }
            bool Event(string name) => Events().Contains($"\"event\":\"{name}\"", StringComparison.Ordinal);
            await WaitFor(() => Event("hook-installed"), "app keyboard hook did not start");

            // The shared LearningTests helper closes Notepad by name, so leave any existing window alone.
            Assert.Empty(Process.GetProcessesByName("notepad"));
            var draft = Path.Combine(root, "draft.txt");
            File.WriteAllText(draft, original);
            using var notepad = new NotepadWindow(draft);
            await WaitFor(() => FocusProbe.LearningTarget.Capture(out _)?.Read()?.Value == original,
                "Notepad did not open the synthetic draft");
            Key(KeyEvent.VkLControl);
            Key((byte)'A'); Key((byte)'A', up: true);
            Key(KeyEvent.VkLControl, up: true);
            var target = FocusProbe.LearningTarget.Capture(out _);
            await WaitFor(() => target?.Read()?.Selection.Length == original.Length, "fixture was not selected");
            var before = target!.Read()!;

            byte trigger = separate ? (byte)'E' : (byte)KeyEvent.VkRControl;
            if (separate) Key(KeyEvent.VkLControl);
            Key(trigger);
            try
            {
                await WaitFor(() => Event("recording-started"), "voice edit did not start recording");
                await Task.Delay(700);
                Key(trigger, up: true);
                if (moveSelection)
                {
                    // Ctrl is still held, so paste must wait. Change the selection only after the edit was staged.
                    await WaitFor(() => ClipboardPaste.ReadText() == edited, "the edit was not staged during the modifier wait");
                    Key(0x27); Key(0x27, up: true);
                    await WaitFor(() => target!.Read()?.Selection.Length == 0, "selection did not move during the modifier wait");
                }
            }
            finally
            {
                Key(trigger, up: true);
                if (separate) Key(KeyEvent.VkLControl, up: true);
            }

            await WaitFor(() => Event("voice-edit") || Event("error"),
                "voice edit did not finish");
            var expectedStatus = moveSelection ? "saved-copy" : "edited";
            Assert.True(Events().Contains($"\"status\":\"{expectedStatus}\"", StringComparison.Ordinal),
                $"expected voice edit status {expectedStatus}");

            string expectedValue = moveSelection ? before.Value : before.Value.Remove(before.Selection.Location, before.Selection.Length)
                .Insert(before.Selection.Location, edited);
            await WaitFor(() => target!.Read()?.Value == expectedValue, "Notepad did not have the expected value");
            await WaitFor(() => ClipboardPaste.ReadText() != edited, "the edited text was left on the clipboard");
            var history = new HistoryStore(MurmurDataLocations.Standard(data).History).Entries().Single();
            Assert.True(history.RawText == instruction && history.WritingModeName == "Voice edit", "instruction History entry missing");
            Assert.True(moveSelection ? history.EditOriginal is null && history.Status == InsertionStatus.SavedNotInserted
                : history.Status == InsertionStatus.Edited && history.EditOriginal == original && history.FinalText == edited,
                "unexpected History status or payload");

            using var requests = JsonDocument.Parse(await http.GetStringAsync("/control/requests"));
            var paths = requests.RootElement.EnumerateArray().Select(item => item.GetProperty("path").GetString()).ToArray();
            Assert.Equal(new[] { "/v1/audio/transcriptions", "/v1/chat/completions" }, paths);
        }
        finally
        {
            foreach (var process in new[] { app, provider })
            {
                if (process is null) continue;
                using (process)
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                }
            }
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class VoiceEditDesktopFactAttribute : FactAttribute
{
    public VoiceEditDesktopFactAttribute()
    {
        if (!SessionFactAttribute.Interactive) Skip = "Needs the interactive desktop session";
        else if (Environment.GetEnvironmentVariable("MURMUR_VOICE_EDIT_TEST_APP") is null ||
            Environment.GetEnvironmentVariable("MURMUR_VOICE_EDIT_TEST_PROVIDER") is null ||
            Environment.GetEnvironmentVariable("MURMUR_VOICE_EDIT_TEST_ROOT") is null)
            Skip = "Needs built Murmur and FakeProvider paths plus an isolated test root";
    }
}
