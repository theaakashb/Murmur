using Murmur.Core;

namespace Murmur.App;

/// <summary>Stand-in until Core's ProcessingCoordinator lands: dictionary, then the local rule cleaner and its guard. Cloud cleanup choices
/// also run here (shortcut: no model call, no modes, no voice cues), recorded as <see cref="CleanupPath.LocalRules"/>.</summary>
internal sealed class RulesOnlyProcessor(Func<DictionaryStore> dictionary) : IDictationProcessor
{
    public Task<DictationResult> ProcessAsync(DictationRequest request, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var store = dictionary();
        var applied = store.Apply(request.RawTranscript);
        var path = !request.Options.AnyEnabled ? CleanupPath.Off : request.Cleanup == CleanupChoice.LocalRules ? CleanupPath.LocalRoute : CleanupPath.LocalRules;
        if (path == CleanupPath.Off) return new DictationResult(applied, path, null, null);
        var outcome = CleanupPipeline.Process(applied, request.Options, new RuleCleaner(), store.VocabularyTerms);
        return outcome switch
        {
            CleanupOutcome.Cleaned c => new DictationResult(c.Text, path, null, null),
            CleanupOutcome.Unchanged u => new DictationResult(u.Text, path, null, null),
            // A dictation is never held: the dictionary-applied text is the last fallback (spec/pipeline.md section 9).
            CleanupOutcome.HeldForRecovery h => new DictationResult(applied, CleanupPath.LocalRules, h.Reason, null),
            _ => new DictationResult(applied, path, null, null),
        };
    }, cancellationToken);
}
