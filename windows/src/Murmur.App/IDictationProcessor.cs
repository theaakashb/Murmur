using Murmur.Core;

namespace Murmur.App;

/// <summary>Everything the processor may look at: the raw transcript and a snapshot taken when the dictation started.</summary>
public sealed record DictationRequest(string RawTranscript, CleanupOptions Options, CleanupChoice Cleanup, string? AppName, string? WritingModeId);

/// <summary>An empty <see cref="FinalText"/> means only filler words were heard: nothing is inserted. <see cref="Fallback"/> is why the full cleanup was not used.</summary>
public sealed record DictationResult(string FinalText, CleanupPath Path, CleanupFailureReason? Fallback, string? RejectedOutput);

/// <summary>Dictionary, voice cues, mode, cleanup and guard for one dictation (spec/pipeline.md sections 6-9). It always returns text: a dictation is never held.</summary>
public interface IDictationProcessor
{
    Task<DictationResult> ProcessAsync(DictationRequest request, CancellationToken cancellationToken);
}
