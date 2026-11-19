// Port of the process side of app/ModelHelper.swift (HelperLaunch, readHelperOutput, the one-shot helper). WarmRunner.cs has the warm process.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Murmur.Core;

/// <summary>The helper's files in a model folder, its output limits and the one-shot (<c>--single</c>) fallback. The helper never receives or prints a transcript on stderr, and the app discards its stderr.</summary>
public static class ModelHelper
{
    /// <summary>Largest helper answer read: 4 MB, as on the Mac.</summary>
    public const int OutputLimit = 4 * 1024 * 1024;
    public const int RequestTimeoutSeconds = 300;

    /// <summary>The four files a model folder must hold (sherpa-onnx NeMo transducer, int8).</summary>
    public static readonly string[] RequiredFiles = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];

    public static bool HasModelFiles(string folder) => folder.Length > 0 && RequiredFiles.All(f => File.Exists(Path.Combine(folder, f)));

    /// <summary>The first JSON object on <paramref name="line"/>, or null.</summary>
    public static JsonObject? Answer(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonNode.Parse(line) as JsonObject; } catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    public static string? StringField(JsonObject? answer, string name) =>
        answer?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Runs a display-only live helper until capture ends. The caller bounds the incoming audio queue.</summary>
    public static async Task StreamAsync(string helper, string folder, ChannelReader<float[]> samples, Action<string> onPartial, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        using var process = new Process { StartInfo = StartInfo(helper, [folder, "--stream", AudioFormat.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)], redirectInput: true) };
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return; }
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var kill = stopping.Token.Register(() => Kill(process));

        async Task Write()
        {
            try
            {
                var input = process.StandardInput.BaseStream;
                await foreach (var block in samples.ReadAllAsync(stopping.Token).ConfigureAwait(false))
                {
                    var bytes = new byte[block.Length * sizeof(float)];
                    Buffer.BlockCopy(block, 0, bytes, 0, bytes.Length);
                    await input.WriteAsync(bytes, stopping.Token).ConfigureAwait(false);
                }
                process.StandardInput.Close();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException) { Kill(process); }
        }

        var writer = Write();
        try
        {
            var pending = new List<byte>();
            while (await ReadLineAsync(process.StandardOutput.BaseStream, pending, stopping.Token).ConfigureAwait(false) is { } line)
                if (StringField(Answer(line), "partial") is { } text && !stopping.IsCancellationRequested) onPartial(text);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
        finally
        {
            stopping.Cancel();
            await writer.ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    public static ProcessStartInfo StartInfo(string helper, IEnumerable<string> arguments, bool redirectInput)
    {
        var info = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = redirectInput, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (redirectInput) info.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var a in arguments) info.ArgumentList.Add(a);
        return info;
    }

    /// <summary>Reads one line, at most <see cref="OutputLimit"/> bytes, from <paramref name="stream"/>; null at end of input or past the limit.
    /// <paramref name="pending"/> carries bytes read past the newline to the next call.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, List<byte> pending, CancellationToken ct)
    {
        var chunk = new byte[64 * 1024];
        while (true)
        {
            int newline = pending.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                if (newline > OutputLimit) return null;
                var line = Encoding.UTF8.GetString(pending.GetRange(0, newline).ToArray());
                pending.RemoveRange(0, newline + 1);
                return line.TrimEnd('\r');
            }
            if (pending.Count > OutputLimit) return null;
            int n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (n == 0) return null;
            pending.AddRange(chunk.AsSpan(0, n).ToArray());
        }
    }

    /// <summary>The one-shot fallback: <c>helper MODELS AUDIO --single [--vocabulary FILE]</c>, one process, at most 300 s. Returns the last JSON line, or null on any failure.</summary>
    public static async Task<string?> RunOnceAsync(string helper, HelperLaunch launch, string audio, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = StartInfo(helper, [launch.Models, audio, "--single", .. launch.Arguments], redirectInput: false) };
        try { process.Start(); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        process.ErrorDataReceived += static (_, _) => { }; // stderr is drained and dropped: it can hold dictionary terms
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
        string? last = null;
        try
        {
            var pending = new List<byte>();
            while (await ReadLineAsync(process.StandardOutput.BaseStream, pending, timeout.Token).ConfigureAwait(false) is { } line) if (line.Length > 0) last = line;
            if (pending.Count > OutputLimit) { Kill(process); return null; }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Kill(process); return null; }
        return process.ExitCode == 0 ? last : null;
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>What the helper loads: the model folder plus, when the dictionary has enabled entries, <c>--vocabulary FILE</c> (and <c>--lexicon FILE</c> once a word list exists).
/// <see cref="Key"/> changes whenever any of those change, which restarts the warm helper. Built only by <see cref="Make"/>, so the warm helper and the one-shot fallback agree.</summary>
public sealed record HelperLaunch(string Models, IReadOnlyList<string> Arguments, string Key)
{
    /// <summary>Null unless the model folder holds the four files. shortcut: presence only; hash verification arrives with the ModelDownload port.</summary>
    public static HelperLaunch? Make(string models, string? vocabularyFile = null, string? lexiconFile = null)
    {
        if (!ModelHelper.HasModelFiles(models)) return null;
        var key = new StringBuilder(Path.GetFullPath(models));
        var arguments = new List<string>();
        foreach (var f in ModelHelper.RequiredFiles) key.Append('|').Append(new FileInfo(Path.Combine(models, f)).Length);
        if (vocabularyFile is not null && File.Exists(vocabularyFile) && File.ReadAllBytes(vocabularyFile) is { Length: > 0 } terms)
        {
            arguments.AddRange(["--vocabulary", vocabularyFile]);
            key.Append('|').Append(Convert.ToHexString(SHA256.HashData(terms)));
            if (lexiconFile is not null && File.Exists(lexiconFile)) { arguments.AddRange(["--lexicon", lexiconFile]); key.Append('|').Append(Path.GetFullPath(lexiconFile)); }
        }
        return new HelperLaunch(models, arguments, key.ToString());
    }

    public bool HasVocabulary => Arguments.Count > 0;
}
