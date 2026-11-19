// Port of app/WelcomeGuide.swift.
namespace Murmur.Core;

public sealed class WelcomeGuide(ISettingsStore settings)
{
    public const int Version = 1;
    public enum Step { Microphone, Accessibility, InputMonitoring, Model, TryIt, Transcription, Cleanup, Meetings, Welcome, RecordShortcut }
    public enum Phase { Welcome, GetReady, FirstWords }
    public const string StepKey = "welcomeGuideStep", CompletedKey = "welcomeGuideCompletedVersion";
    const string ReviewedKey = "welcomeGuideReviewedChoices";

    public static IReadOnlyList<Step> Steps(bool missingRightCtrl = false) =>
        missingRightCtrl ? [Step.Welcome, Step.Microphone, Step.RecordShortcut, Step.Transcription, Step.Cleanup, Step.Model, Step.TryIt]
            : [Step.Welcome, Step.Microphone, Step.Transcription, Step.Cleanup, Step.Model, Step.TryIt];

    public static bool IsConfiguration(Step step) => step is Step.Transcription or Step.Cleanup;
    public static Phase PhaseFor(Step step) => step switch
    {
        Step.Welcome => Phase.Welcome,
        Step.TryIt => Phase.FirstWords,
        _ => Phase.GetReady,
    };

    IReadOnlyList<double> Reviewed => settings.Get(ReviewedKey) as IReadOnlyList<double> ?? [];

    public void Confirm(Step step)
    {
        if (IsConfiguration(step) && !IsConfirmed(step)) settings.Set(ReviewedKey, Reviewed.Append((double)step).ToArray());
    }

    public bool IsConfirmed(Step step) => Reviewed.Contains((int)step);

    public Step? NextUnconfirmed() => Steps().Where(step => IsConfiguration(step) && !IsConfirmed(step)).Select(step => (Step?)step).FirstOrDefault();

    public Step ResumeStep(bool missingRightCtrl = false)
    {
        if (settings.Get(StepKey) is not { } raw) return Step.Welcome;
        var saved = new Setting<int>(StepKey, (int)Step.Welcome).Read(raw);
        return (Step)saved switch
        {
            Step.Accessibility or Step.InputMonitoring => missingRightCtrl ? Step.RecordShortcut : Step.Transcription,
            Step.RecordShortcut when !missingRightCtrl => Step.Transcription,
            Step.Meetings => Step.Model,
            var step when Enum.IsDefined(step) => step,
            _ => saved < 0 ? Step.Welcome : Step.TryIt,
        };
    }

    public void Save(Step step) => settings.Set(StepKey, (int)step);

    public int CompletedVersion(bool alreadySetUp) => settings.Get(CompletedKey) is { } raw
        ? new Setting<int>(CompletedKey, 0).Read(raw) : alreadySetUp && settings.Get(StepKey) is null ? 1 : 0;

    public bool OpensAtLaunch(bool alreadySetUp)
    {
        var done = CompletedVersion(alreadySetUp);
        if (done > 0 && settings.Get(CompletedKey) is null) settings.Set(CompletedKey, done);
        return done < Version;
    }

    public void Complete()
    {
        settings.Set(CompletedKey, Version);
        settings.Remove(StepKey);
    }

    public void Replay()
    {
        settings.Remove(ReviewedKey);
        Save(Step.Welcome);
    }
}
