namespace Murmur.Core;

/// <summary>Which model finished downloading.</summary>
public enum ModelNoticeKind { Speech, Cleanup }

/// <summary>Where a click on the "Murmur is ready" notification goes.</summary>
public enum ModelNoticeTarget { Welcome, Home }

/// <summary>The words and the decisions of the Welcome model steps and the download-complete notification, in one place so the copy is easy to change.
/// Windows only: the Mac has no download notification, and its Speech model step is a "Download…" button plus the size line (app/WelcomeGuideView.swift:25, :192-196).</summary>
public static class OnboardingText
{
    // ---- the download-complete notification (a mock-up pending the user's approval) ----
    public const string NoticeTitle = "Murmur is ready";
    public const string NoticeButton = "Open Murmur";

    public static string NoticeBody(ModelNoticeKind kind, string modelName, string shortcut) => kind == ModelNoticeKind.Speech
        ? $"{modelName} finished downloading. Hold {shortcut} to start dictating."
        : $"{modelName} finished downloading. Cleanup is on.";

    /// <summary>Only a finished download notifies (a failed or cancelled one carries an error), and only when the user is not already looking at a Murmur window.</summary>
    public static bool ShouldNotify(ModelDownloadError? error, bool murmurIsForeground) => error is null && !murmurIsForeground;

    /// <summary>The Welcome window if it is open, else Home.</summary>
    public static ModelNoticeTarget NoticeTarget(bool welcomeOpen) => welcomeOpen ? ModelNoticeTarget.Welcome : ModelNoticeTarget.Home;

    // ---- the Welcome "Speech model" step ----
    public static string DownloadButton(string modelName, string size) => size.Length > 0 ? $"Download {modelName} ({size})" : $"Download {modelName}";
    public const string DownloadHint = "Murmur downloads it in the background. You can keep going, and it will tell you when it is ready.";
    public const string DownloadingHint = "Downloading. You can continue to the next step; Murmur will tell you when it is ready.";
    public const string ReadyHint = "The model is on this PC. Continue when you are ready.";
    public const string ContinueWhileDownloading = "Continue →";

    // ---- the Welcome "Cleanup" step when the chosen languages have no cleanup to set ----
    public const string NothingToSetTitle = "Nothing to set here";
    public const string NothingToSetBody = "Cleanup stays off for these languages, so there is no choice to make. Press Use These Settings to continue.";
}
