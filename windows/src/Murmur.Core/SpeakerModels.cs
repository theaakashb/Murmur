// Speaker separation models (Windows): pyannote segmentation-3.0 and a WeSpeaker embedding, each its own pinned download, run by `MurmurSpeech --diarize`.
// The Mac's single Core ML pipeline (FluidInference/speaker-diarization-coreml) has no ONNX counterpart.
using System.Diagnostics;
using System.Globalization;

namespace Murmur.Core;

public sealed partial record ModelFacts
{
    public static readonly ModelFacts SpeakerSegmentation = new()
    {
        DisplayName = "Speaker segmentation",
        RowTitle = "pyannote segmentation 3.0",
        Repository = "csukuangfj/sherpa-onnx-pyannote-segmentation-3-0",
        Source = ModelPinsSource.WindowsModels,
        ConfirmName = "the speaker segmentation model",
        SummaryLead = "pyannote segmentation 3.0",
        SummaryTail = ["MIT"],
        Placement = SavedIn,
        RemoveTitle = "Remove the speaker segmentation model?",
        RemoveBody = (folder, _) => $"Meetings stop separating speakers until you download it again. This deletes {folder}.",
        Completed = "The speaker segmentation model is downloaded and verified.",
        Removed = "The speaker segmentation model was removed.",
        Explanation = SpeakerFacts.Explanation(false),
    };

    public static readonly ModelFacts SpeakerEmbedding = new()
    {
        DisplayName = "Speaker embedding",
        RowTitle = "WeSpeaker ResNet34",
        Repository = "csukuangfj/speaker-embedding-models",
        Source = ModelPinsSource.WindowsModels,
        FolderName = "wespeaker-en-voxceleb-resnet34",
        ConfirmName = "the speaker embedding model",
        SummaryLead = "WeSpeaker VoxCeleb ResNet34",
        SummaryTail = ["Apache-2.0"],
        Placement = SavedIn,
        RemoveTitle = "Remove the speaker embedding model?",
        RemoveBody = (folder, _) => $"Meetings stop separating speakers until you download it again. This deletes {folder}.",
        Completed = "The speaker embedding model is downloaded and verified.",
        Removed = "The speaker embedding model was removed.",
        Explanation = SpeakerFacts.Explanation(false),
    };

    public const string SpeakerEmbeddingFile = "wespeaker_en_voxceleb_resnet34.onnx", SpeakerSegmentationFile = "model.onnx";
}

/// <summary>Runs `MurmurSpeech SEGMENTATION_FOLDER --diarize KEPT_WAV THRESHOLD --embedding EMBEDDING_ONNX` once per meeting (port of SpeakerRunner in app/MeetingSpeakers.swift).
/// The process exits when it is done, so its memory goes back; cancelling kills it.</summary>
public static class SpeakerRunner
{
    /// <summary>A 2-hour meeting took 32 s on the Mac; far beyond that, give up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    /// <returns>Null when the helper failed, timed out or answered nonsense. A cancelled run throws <see cref="OperationCanceledException"/>.</returns>
    public static async Task<IReadOnlyList<SpeakerSegment>?> RunAsync(string helper, string segmentationFolder, string embeddingModel, string audio, double threshold,
        Func<bool> check, CancellationToken cancellationToken)
    {
        if (!await Task.Run(check, cancellationToken).ConfigureAwait(false)) return null;
        var start = new ProcessStartInfo(helper) { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { segmentationFolder, "--diarize", audio, threshold.ToString("R", CultureInfo.InvariantCulture), "--embedding", embeddingModel }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync(cancellationToken); // never read; drained so a full pipe cannot stall the helper
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            var reply = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? MeetingSpeakers.Segments(System.Text.Encoding.UTF8.GetBytes(reply)) : null;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            return null; // the timeout
        }
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
