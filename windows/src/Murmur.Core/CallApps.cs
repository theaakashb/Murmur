// Port of app/CallApps.swift with Windows process identities.
using System.Text.Json.Serialization;

namespace Murmur.Core;

[JsonConverter(typeof(JsonStringEnumConverter<MeetingStream>))]
public enum MeetingStream
{
    [JsonStringEnumMemberName("me")] Me,
    [JsonStringEnumMemberName("them")] Them,
}

public sealed record MeetingSource(string Name, string? BundleID = null, bool AllApps = false, bool MicOnly = false)
{
    [JsonIgnore] public MeetingStream SpeakerStream => MicOnly ? MeetingStream.Me : MeetingStream.Them;
    public static readonly MeetingSource Everything = new("All apps", AllApps: true);
    public static readonly MeetingSource MicrophoneOnly = new("Only my microphone", MicOnly: true);
}

public sealed record CallApp(string ProcessName, string Name, bool IsBrowser = false)
{
    public static IReadOnlyList<CallApp> Catalog { get; } = Array.AsReadOnly<CallApp>(
    [
        new("zoom", "Zoom"), new("ms-teams", "Microsoft Teams"), new("teams", "Microsoft Teams"),
        new("slack", "Slack"), new("webex", "Webex"), new("ciscocollabhost", "Webex"),
        new("discord", "Discord"), new("whatsapp", "WhatsApp"), new("signal", "Signal"), new("telegram", "Telegram"),
        new("chrome", "Google Chrome", true), new("msedge", "Microsoft Edge", true), new("brave", "Brave", true),
        new("firefox", "Firefox", true), new("arc", "Arc", true), new("opera", "Opera", true),
    ]);

    public static string Normalize(string name) => (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).ToLowerInvariant();
    public static CallApp? Find(string name) => Catalog.FirstOrDefault(app => app.ProcessName == Normalize(name));
}
