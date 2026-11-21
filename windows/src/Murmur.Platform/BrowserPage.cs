namespace Murmur.Platform;

/// <summary>Website matching uses the host and path only. Browser UIA values never reach History, diagnostics or providers.</summary>
internal static class BrowserPage
{
    internal enum Family { Chromium, Firefox }

    internal static Family? Supported(string processName) => processName switch
    {
        "msedge" or "chrome" or "brave" or "vivaldi" or "opera" or "arc" or "chromium" or "comet" => Family.Chromium,
        "firefox" => Family.Firefox,
        _ => null,
    };

    /// <summary>UI Automation control types for a Window and a Pane: what focus looks like before Chromium has built the page tree.</summary>
    internal static bool IsBareWindowFocus(int controlType) => controlType is 50032 or 50033;

    internal static bool CanRead(bool insideDocument, bool addressBarFocused, bool password) =>
        insideDocument && !addressBarFocused && !password;

    internal static Uri? Parse(string? value, bool schemeOptional)
    {
        if (value is null || value.Length > 8192) return null;
        value = value.Trim();
        if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && IsWeb(absolute)) return ForMatching(absolute);
        if (!schemeOptional || value.Contains("://", StringComparison.Ordinal) || value.StartsWith('/')) return null;
        // The omnibox can hide the scheme. Require a domain, a loopback name or an IP to avoid interpreting search text as a URL.
        return Uri.TryCreate("https://" + value, UriKind.Absolute, out var address) && IsWeb(address) &&
            (address.Host.Contains('.') || address.IsLoopback || address.HostNameType == UriHostNameType.IPv6) ? ForMatching(address) : null;
    }

    static bool IsWeb(Uri uri) => uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    static Uri ForMatching(Uri uri) => new(uri.GetLeftPart(UriPartial.Path));
}
