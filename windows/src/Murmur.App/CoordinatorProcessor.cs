using Murmur.Core;

namespace Murmur.App;

/// <summary>Core's ProcessingCoordinator behind the app's processor seam: dictionary, voice cues, cleanup route, guard and fallbacks (spec/pipeline.md sections 6-9).</summary>
internal sealed class CoordinatorProcessor(Func<ProcessingCoordinator?> coordinator) : IDictationProcessor
{
    public async Task<DictationResult> ProcessAsync(DictationRequest request, CancellationToken cancellationToken)
    {
        if (coordinator() is not { } p) return new DictationResult(request.RawTranscript, CleanupPath.Off, null, null, Held: true);
        p.Update(p.Settings with { Options = request.Options, Route = request.Cleanup.ProcessingRoute() }, persist: false);
        p.CloudCleaner = request.Cleaner;
        p.EngineCleaner = request.Engine;
        var style = p.Settings.Style(request.Mode);
        if (!request.Cleanup.CloudOnlyOK())
            style = style with { Options = request.Cleanup.EffectiveOptions(style.Options), Context = null };
        var result = p.Settings.Route == CleanupRoute.Local ? p.Process(request.RawTranscript, style) : await p.ProcessAsync(request.RawTranscript, style, cancellationToken);
        // Held only happens when processing is unavailable; a dictation is never held, so the raw transcript goes out (spec/pipeline.md section 9).
        return result.Outcome switch
        {
            ProcessingOutcome.Insert i => new DictationResult(i.Text, result.CleanupPath ?? CleanupPath.Off, result.Fallback, result.RejectedOutput),
            ProcessingOutcome.HeldForRecovery h => new DictationResult(h.Raw, CleanupPath.LocalRules, h.Reason, result.RejectedOutput),
            _ => new DictationResult(request.RawTranscript, CleanupPath.LocalRules, CleanupFailureReason.CleanerFailed, null),
        };
    }
}
