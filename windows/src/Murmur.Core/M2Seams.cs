// Minimal stand-ins for types the M2 provider and route port needs from Swift files other workers (or later milestones) port.
// Each entry names the Swift file it belongs to; the coordinator replaces it with that file's port and deletes it here.
namespace Murmur.Core;

/// <summary>Belongs to app/Processing.swift. Where cleanup runs: <c>Local</c> is the RuleCleaner, <c>Cloud</c> is through an
/// <see cref="ICleanupProvider"/>. Raw values "local" and "cloud".</summary>
public enum CleanupRoute { Local, Cloud }

/// <summary>Belongs to app/Retention.swift. The 24-hour retry-audio window; user text that names it is built from <see cref="Text"/>.</summary>
public static class RetryWindow
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    public static readonly string Text = $"{(int)Lifetime.TotalHours} hours";
}

/// <summary>Belongs to app/LocalCleanup.swift (M3).</summary>
public static class LocalCleanupIdentity
{
    public const string ModelName = "Vedu Scribe 0.8B";
    /// <summary>Recorded in History: the pinned repository and the first 7 characters of its revision.</summary>
    public const string ModelID = "heyvedu/vedu-scribe-0.8b@7dced58";
}

/// <summary>Belongs to app/LocalCleanup.swift (M3): the managed local cleanup model, with its Swift shape, model id and disclosure.
/// shortcut: the local model runner is not ported until M3, so the model always reports not downloaded and cleanup throws
/// modelUnavailable(localModel, modelNotDownloaded); dictations then take the local-rules fallback. Replace with the LocalCleanup port in M3.</summary>
public sealed record LocalModelCleanupProvider : ICleanupProvider
{
    public ProviderID Id => ProviderID.LocalModel;
    /// <summary>Null when the model and helper are installed.</summary>
    public OnDeviceModelUnavailable? Status() => OnDeviceModelUnavailable.ModelNotDownloaded;
    public string? ModelID => LocalCleanupIdentity.ModelID;
    /// <summary>One fixed prompt: no screen context or style notes, and rewrite presets fall back to local rules.</summary>
    public CleanupShape Shape => new() { TakesContext = false, SectionTarget = 150, KeepsCorrectionsAttached = true, TakesPresets = false };
    public ICleanupProvider ForMeetingNotes() => this;
    public DataDisclosure Disclosure => new(Id, ProcessingStage.Cleanup, $"{LocalCleanupIdentity.ModelName}, downloaded to this Mac", [], ["the transcript"], "");

    /// <summary>Rewrite presets and notes carry no typed input: this model cannot follow them, so they are refused, not approximated.</summary>
    public Task<string> CleanupAsync(CleanupMessages messages, CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new ProviderException(messages.Input is null ? ProviderError.InvalidConfiguration(Id) : ProviderError.ModelUnavailable(Id, Status()!.Value)));

    public Task ValidateAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new ProviderException(ProviderError.ModelUnavailable(Id, Status()!.Value)));
}
