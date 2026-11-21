using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Murmur.Core;
using Murmur.Platform;
using Xunit.Abstractions;

namespace Murmur.Platform.Tests;

public sealed class BrowserPageTests
{
    [Fact]
    public void ParsesWebHostsAndKeepsOnlyTheMatchingPath()
    {
        var page = BrowserPage.Parse("https://EXAMPLE.test:8443/team/%66eed?token=fixture#anchor", schemeOptional: false);
        Assert.True(page is { Host: "example.test", Port: 8443, Query.Length: 0, Fragment.Length: 0 }, "host parsing or metadata removal");
        Assert.True(WritingModes.WebsiteMatches("example.test/team/feed", page!), "decoded path rule");
        Assert.False(WritingModes.WebsiteMatches("example.test/team/feedback", page!), "path segment boundary");
        Assert.True(BrowserPage.Parse("example.test/team", schemeOptional: true)?.Host == "example.test", "scheme omitted by omnibox");
        Assert.True(BrowserPage.Parse("localhost:1234/team", schemeOptional: true)?.Host == "localhost", "loopback dev server");
        Assert.True(BrowserPage.Parse("example.test/team", schemeOptional: false) is null, "document requires a scheme");
    }

    [Fact]
    public void RejectsNonWebOrAmbiguousValues()
    {
        string[] invalid = ["hello world", "about:blank", "file:///C:/fixture.html", "javascript:void(1)",
            "https://user@example.test", "https://example.test\\other", "custom://example.test", "example", "\n", "//example.test", "https://a.test/\nb"];
        foreach (var candidate in invalid)
            Assert.True(BrowserPage.Parse(candidate, schemeOptional: true) is null, "invalid address parsed");
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    public void OnlyNonsecurePageFocusCanRead(bool inPage, bool addressBar, bool password, bool expected) =>
        Assert.Equal(expected, BrowserPage.CanRead(inPage, addressBar, password));

    [Theory]
    [InlineData(50004, true)]  // Chromium omnibox: Edit
    [InlineData(50003, true)]  // Firefox urlbar-input: ComboBox (Firefox 160 UI Automation)
    [InlineData(50030, false)] // a page document is never the address bar
    [InlineData(50000, false)]
    public void AddressBarControlTypes(int controlType, bool expected) => Assert.Equal(expected, FocusProbe.IsAddressBarControl(controlType));

    [Theory]
    [InlineData(50032, true)]  // Window, Pane: focus before Chromium builds its tree
    [InlineData(50033, true)]
    [InlineData(50004, false)]
    [InlineData(50030, false)]
    public void BareWindowFocusIsBeforeTheTreeExists(int controlType, bool expected) => Assert.Equal(expected, BrowserPage.IsBareWindowFocus(controlType));

    [Theory]
    [InlineData("comet", true)]
    [InlineData("chrome", true)]
    [InlineData("firefox", true)]
    [InlineData("notepad", false)]
    public void SupportedBrowsers(string process, bool expected) => Assert.Equal(expected, BrowserPage.Supported(process) is not null);

    [Fact]
    public void WebsiteModeWinsAndMissingPageFallsBackToAppMode()
    {
        var app = new WritingMode { Apps = ["msedge"] };
        var site = new WritingMode { Websites = ["example.test/work"] };
        var modes = new[] { app, site };
        Assert.Same(site, WritingModes.Match(modes, "msedge", BrowserPage.Parse("https://example.test/work", false)));
        Assert.Same(app, WritingModes.Match(modes, "msedge", null));
        Assert.False(WritingModes.NeedsURL([site with { Enabled = false }, app]));
    }
}

/// <summary>Opens synthetic loopback pages in an isolated browser profile. Never writes URLs to test output.</summary>
[Collection("Browser desktop")]
public sealed class ChromiumBrowserPageTests(ITestOutputHelper output)
{
    [DllImport("user32.dll")] static extern void keybd_event(byte virtualKey, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(nint h, bool altTab);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(nint hwnd);

    // A background process may not take focus; attaching to the foreground thread's input queue lifts that lock.
    static bool Activate(nint hwnd)
    {
        uint other = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
        bool attached = other != me && AttachThreadInput(me, other, true);
        try
        {
            BringWindowToTop(hwnd);
            if (SetForegroundWindow(hwnd)) return true;
            SwitchToThisWindow(hwnd, true);
            if (GetForegroundWindow() == hwnd) return true;
            ShowWindow(hwnd, 6); ShowWindow(hwnd, 9); // minimize, then restore
            return SetForegroundWindow(hwnd) || GetForegroundWindow() == hwnd;
        }
        finally { if (attached) AttachThreadInput(me, other, false); }
    }

    static void Press(byte key, bool control = false)
    {
        if (control) keybd_event(0x11, 0, 0, 0);
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
    }

    [BrowserDesktopFact]
    public async Task ReadsFocusedPageAndSkipsAddressBar()
    {
        var processName = Environment.GetEnvironmentVariable("MURMUR_BROWSER_PROBE") ??
            (Environment.GetEnvironmentVariable("MURMUR_BROWSER_PROBE_CHROME") == "1" ? "chrome" : "msedge");
        bool firefox = processName == "firefox";
        var relative = processName switch
        {
            "msedge" => @"Microsoft\Edge\Application\msedge.exe",
            "chrome" => @"Google\Chrome\Application\chrome.exe",
            "brave" => @"BraveSoftware\Brave-Browser\Application\brave.exe",
            "firefox" => @"Mozilla Firefox\firefox.exe",
            _ => throw new ArgumentException("Unknown browser probe target")
        };
        var exe = Environment.GetEnvironmentVariable("MURMUR_BROWSER_PROBE_EXE") ??
            new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
            .Select(Environment.GetFolderPath)
            .Select(root => Path.Combine(root, relative)).FirstOrDefault(File.Exists);
        Assert.True(File.Exists(exe), "Browser executable unavailable");

        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var serving = ServePages(server, stop.Token);
        string profile = Path.Combine(Path.GetTempPath(), "murmur-browser-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Process? browser = null;
        try
        {
            if (firefox)
                File.WriteAllText(Path.Combine(profile, "user.js"), """
                    user_pref("browser.aboutwelcome.enabled", false);
                    user_pref("browser.shell.checkDefaultBrowser", false);
                    """);
            browser = Launch(exe!, profile, $"http://127.0.0.1:{port}/first", "--new-window");
            Assert.True(await WaitForPage(processName, "/first", startWindow: browser), "normal window did not match its active page");
            Assert.True(FocusProbe.ReadBrowserPage(FocusProbe.Capture(false), out bool addressBar) is not null, "normal window read vanished");
            output.WriteLine(addressBar ? "Normal: address ValuePattern" : "Normal: document value");
            output.WriteLine($"Foreground DPI: {GetDpiForWindow(GetForegroundWindow())}");
            Assert.True(await WaitForPage(processName, "/first", addressOnly: true), "address ValuePattern fallback did not match");
            output.WriteLine("Address ValuePattern: matched");

            var pageFocus = FocusProbe.Capture(readText: false);
            Assert.NotNull(pageFocus);
            Assert.True(await FocusAddressBar(processName), "address bar did not take focus");
            Assert.True(FocusProbe.ReadBrowserPage(FocusProbe.Capture(false)) is null, "address bar focus must not read a page");
            Assert.True(FocusProbe.ReadBrowserPage(pageFocus) is null, "stale page focus must not read a page");
            output.WriteLine("Address-bar and stale-focus gates: passed");

            Press(0x1b); Press(0x1b); // Chrome keeps omnibox focus after the first Escape.
            using var tab = Launch(exe!, profile, $"http://127.0.0.1:{port}/tab", firefox ? ["--new-tab"] : []);
            Assert.True(await WaitForPage(processName, "/tab"), "new tab did not match its active page");
            Press(0x09, control: true); // Ctrl+Tab returns to the first tab.
            Assert.True(await WaitForPage(processName, "/first"), "tab switch did not match the first page");
            output.WriteLine("Tab switch: matched");

            Press(0x7a); // F11
            await Task.Delay(500);
            bool fullScreenMatched = await WaitForPage(processName, "/first", seconds: firefox ? 3 : 18);
            Press(0x7a);
            if (!firefox) Assert.True(fullScreenMatched, "full screen did not match document with toolbar hidden");
            output.WriteLine(fullScreenMatched ? "Full screen: matched" : "Full screen: no match");

            using var second = Launch(exe!, profile, $"http://127.0.0.1:{port}/second", "--new-window");
            Assert.True(await WaitForPage(processName, "/second"), "second window did not match its own page");
            output.WriteLine("Second window: matched");

            using var embedded = Launch(exe!, profile, $"http://127.0.0.1:{port}/embedded", "--new-window");
            Assert.True(await WaitForPage(processName, "/embedded"), "focused iframe did not match its top-level page");
            output.WriteLine("Iframe page: matched");

            string[] privateFlags = processName switch
            {
                "firefox" => ["--private-window"],
                "msedge" => ["--inprivate", "--new-window"],
                _ => ["--incognito", "--new-window"]
            };
            using var privateWindow = Launch(exe!, profile, $"http://127.0.0.1:{port}/private", privateFlags);
            Assert.True(await WaitForPage(processName, "/private"), "private window did not match its own page");
            output.WriteLine("Private window: matched");

            if (!firefox)
            {
                using var app = Launch(exe!, profile, null, $"--app=http://127.0.0.1:{port}/app");
                Assert.True(await WaitForPage(processName, "/app"), "standalone app window did not match its page");
                output.WriteLine("App window: matched");
            }
        }
        finally
        {
            if (browser is { HasExited: false })
            {
                browser.Kill(entireProcessTree: true);
                await browser.WaitForExitAsync();
            }
            browser?.Dispose();
            await stop.CancelAsync();
            await serving;
            for (int attempt = 0; Directory.Exists(profile); attempt++)
            {
                try { Directory.Delete(profile, recursive: true); }
                catch (IOException) when (attempt < 10) { await Task.Delay(100); }
            }
        }
    }

    static async Task<bool> FocusAddressBar(string processName)
    {
        var window = GetForegroundWindow();
        if (GetWindowThreadProcessId(window, out uint pid) == 0) return false;
        using var process = Process.GetProcessById((int)pid);
        if (process.ProcessName != processName) return false;

        var wait = Stopwatch.StartNew();
        var nextPress = TimeSpan.Zero;
        while (wait.Elapsed < TimeSpan.FromSeconds(5) && GetForegroundWindow() == window)
        {
            if (FocusProbe.IsBrowserAddressBarFocused(processName)) return true;
            if (wait.Elapsed >= nextPress)
            {
                Press(0x4c, control: true); // Ctrl+L
                nextPress = wait.Elapsed + TimeSpan.FromSeconds(1);
            }
            await Task.Delay(100);
        }
        return false;
    }

    static Process Launch(string exe, string profile, string? page, params string[] flags)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] profileArgs = Path.GetFileName(exe).Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
            ? ["--wait-for-browser", "--profile", profile]
            : ["--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-extensions", "--disable-search-engine-choice-screen"];
        foreach (var arg in profileArgs.Concat(flags))
            info.ArgumentList.Add(arg);
        if (page is not null) info.ArgumentList.Add(page);
        var process = Process.Start(info)!;
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    static async Task<bool> WaitForPage(string processName, string path, bool addressOnly = false, Process? startWindow = null, int seconds = 18)
    {
        var wait = Stopwatch.StartNew();
        var app = new WritingMode { Apps = [processName] };
        var site = new WritingMode { Websites = ["127.0.0.1" + path] };
        while (wait.Elapsed.TotalSeconds < seconds)
        {
            if (FocusProbe.Capture(false) is { } focus && focus.ProcessName == processName)
            {
                var page = FocusProbe.ReadBrowserPage(focus, out _, preferDocumentValue: !addressOnly);
                if (ReferenceEquals(WritingModes.Match([app, site], processName, page), site)) return true;
            }
            if (!(FocusProbe.Capture(false) is { } f2 && f2.ProcessName == processName) && startWindow is { HasExited: false })
            {
                startWindow.Refresh();
                if (startWindow.MainWindowHandle != 0) Activate(startWindow.MainWindowHandle);
            }
            await Task.Delay(150);
        }
        return false;
    }

    static async Task ServePages(TcpListener server, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient accepted;
                try { accepted = await server.AcceptTcpClientAsync(ct); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                using var client = accepted;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    using var stream = client.GetStream();
                    using var request = new StreamReader(stream, leaveOpen: true);
                    string? requestLine = await request.ReadLineAsync(deadline.Token);
                    while (await request.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
                    const string prefix = "<!doctype html><title>Murmur website probe</title>";
                    var html = prefix + (requestLine?.StartsWith("GET /embedded ", StringComparison.Ordinal) == true
                        ? """<iframe src="/frame" onload="this.contentWindow.focus(); this.contentDocument.querySelector('input').focus()"></iframe>"""
                        : "<label>Field <input autofocus></label>");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n{html}"), deadline.Token);
                }
                catch (IOException) { /* Chromium may abandon a preconnected socket. */ }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
    }
}

[CollectionDefinition("Browser desktop", DisableParallelization = true)]
public sealed class BrowserDesktopCollection;

public sealed class BrowserDesktopFactAttribute : FactAttribute
{
    public BrowserDesktopFactAttribute()
    {
        if (!SessionFactAttribute.Interactive || Environment.GetEnvironmentVariable("MURMUR_RUN_BROWSER_PROBE") != "1")
            Skip = "Run this in the interactive console with MURMUR_RUN_BROWSER_PROBE=1.";
    }
}
