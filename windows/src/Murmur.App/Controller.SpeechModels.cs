using Murmur.Core;

namespace Murmur.App;

// The seam between the session logic (Controller.cs) and the local speech models: which model a language selection needs,
// whether it is installed, where it lives, and how a recording is transcribed with it. ParakeetEnglish behaves as it always has.
internal sealed partial class Controller
{
    /// <summary>True when the model's files are installed and verified, so a session may start with it.</summary>
    bool SpeechModelReady(LocalSpeechModel model) => model == LocalSpeechModel.ParakeetEnglish && s.Local.ModelFilesPresent;

    /// <summary>The folder holding the model's files, or null when this model has no folder yet.</summary>
    string? SpeechModelPath(LocalSpeechModel model) => model == LocalSpeechModel.ParakeetEnglish ? s.Local.ModelDirectory : null;

    /// <summary>The History "transcription model" for a local dictation (Mac: <c>localModel.facts.repository</c>).</summary>
    string SpeechModelRepository(LocalSpeechModel model) =>
        model == LocalSpeechModel.ParakeetEnglish ? LocalTranscriber.ModelId : throw new InvalidOperationException("The model's repository is not known yet.");

    /// <summary>Transcribes the recording with the session's local model.</summary>
    Task<(CloudTranscriptionOutcome Outcome, CloudTranscriptionDetail Detail)> TranscribeLocalAsync(Session ss, float[] samples16k, CancellationToken ct) =>
        ss.Languages.LocalModel == LocalSpeechModel.ParakeetEnglish
            ? s.Local.TranscribeAsync(samples16k, ss.WavPath, ss.Chunks, ct)
            : throw new InvalidOperationException("This speech model is not available yet.");
}
