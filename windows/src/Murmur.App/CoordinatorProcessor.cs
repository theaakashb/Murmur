using Murmur.Core;

namespace Murmur.App;

/// <summary>Core's ProcessingCoordinator behind the app's processor seam: dictionary, voice cues, cleanup route, guard and fallbacks (spec/pipeline.md sections 6-9).</summary>
internal sealed class CoordinatorProcessor(Func<ProcessingCoordinator> coordinator) : IDictationProcessor
{
    public async Task<DictationResult> ProcessAsync(DictationRequest request, CancellationToken cancellationToken)
    {
        var p = coordinator();
        p.Update(p.Settings with { Options = request.Options, Route = request.Cleanup.ProcessingRoute() }, persist: false);
        p.CloudCleaner = request.Cleaner;
        p.EngineCleaner = null; // shortcut: writing-mode engines arrive with modes in M4
        var style = p.Settings.Style();
        var result = p.Settings.Route == CleanupRoute.Local ? p.Process(request.RawTranscript, style) : await p.ProcessAsync(request.RawTranscript, style, cancellationToken);
        return new DictationResult(result.Text, result.CleanupPath ?? CleanupPath.Off, result.Fallback, result.RejectedOutput);
    }
}
