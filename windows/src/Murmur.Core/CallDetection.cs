// Port of app/CallDetection.swift.
namespace Murmur.Core;

public static class CallDetectionSettings
{
    const string EnabledKey = "callDetectionEnabled", NeverKey = "callDetectionNever";
    public static bool Enabled(ISettingsStore settings) => settings.Get(EnabledKey) as bool? ?? true;
    public static void SetEnabled(ISettingsStore settings, bool on) => settings.Set(EnabledKey, on);
    public static IReadOnlyList<string> Never(ISettingsStore settings) => settings.Get(NeverKey) as IReadOnlyList<string> ?? [];
    public static void AddNever(ISettingsStore settings, string process) =>
        settings.Set(NeverKey, Never(settings).Append(process).Select(CallApp.Normalize).Distinct().ToArray());
    public static void RemoveNever(ISettingsStore settings, string process) =>
        settings.Set(NeverKey, Never(settings).Where(name => CallApp.Normalize(name) != CallApp.Normalize(process)).ToArray());
}

public sealed record CallOffer(CallApp App)
{
    public string Text => $"{App.Name} is using the microphone.";
    public string Question => App.IsBrowser ? "If this is a call, take meeting notes?" : "Take meeting notes?";
    public string NeverLabel => $"Never for {App.Name}";
    public MeetingSource Source => new(App.Name, App.ProcessName);
}

public sealed class CallDetector
{
    public const double MinimumInput = 5, SessionGrace = 15, OfferLifetime = 30;
    public sealed record Conditions(bool Enabled = true, IReadOnlyList<string>? Never = null,
        bool MeetingLive = false, bool Dictating = false, IReadOnlySet<string>? OwnProcesses = null);

    sealed record Session(double Since, double LastSeen, bool Offered = false);
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);

    public void Rearm(string process)
    {
        var key = CallApp.Normalize(process);
        if (sessions.TryGetValue(key, out var session)) sessions[key] = session with { Offered = false };
    }

    public CallOffer? Update(IEnumerable<string> inputs, double now, Conditions? conditions = null)
    {
        conditions ??= new();
        var own = conditions.OwnProcesses?.Select(CallApp.Normalize).ToHashSet();
        var seen = inputs.Select(CallApp.Normalize).Where(id => own?.Contains(id) != true)
            .Distinct().Select(CallApp.Find).OfType<CallApp>().OrderBy(app => app.ProcessName, StringComparer.Ordinal).ToArray();

        foreach (var app in seen)
        {
            var key = app.ProcessName;
            sessions[key] = sessions.TryGetValue(key, out var prior) ? prior with { LastSeen = now } : new(now, now);
        }

        foreach (var key in sessions.Keys.ToArray())
        {
            if (now - sessions[key].LastSeen >= SessionGrace) sessions.Remove(key);
            else if (conditions.MeetingLive) sessions[key] = sessions[key] with { Offered = true };
        }
        if (conditions.MeetingLive || !conditions.Enabled || conditions.Dictating) return null;

        var never = conditions.Never?.Select(CallApp.Normalize).ToHashSet();
        foreach (var app in seen)
        {
            var key = app.ProcessName;
            var session = sessions[key];
            if (session.Offered || now - session.Since < MinimumInput || never?.Contains(key) == true) continue;
            sessions[key] = session with { Offered = true };
            return new(app);
        }
        return null;
    }
}
