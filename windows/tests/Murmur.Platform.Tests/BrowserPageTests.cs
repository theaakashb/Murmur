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

/// <summary>Opens synthetic loopback pages in an isolated Chromium profile. Never writes URLs to test output.</summary>
[Collection("Browser desktop")]
public sealed class ChromiumBrowserPageTests(ITestOutputHelper output)
{
    [DllImport("user32.dll")] static extern void keybd_event(byte virtualKey, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);

    static void Press(byte key, bool control = false)
    {
        if (control) keybd_event(0x11, 0, 0, 0);
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2, 0);
        if (control) keybd_event(0x11, 0, 2, 0);
    }

    [InputFact]
    public async Task ReadsFocusedPageAndSkipsAddressBar()
    {
        bool chrome = Environment.GetEnvironmentVariable("MURMUR_BROWSER_PROBE_CHROME") == "1";
        var processName = chrome ? "chrome" : "msedge";
        var relative = chrome ? @"Google\Chrome\Application\chrome.exe" : @"Microsoft\Edge\Application\msedge.exe";
        var exe = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
            .Select(root => Path.Combine(root, relative)).FirstOrDefault(File.Exists);
        Assert.True(exe is not null, "Chromium executable unavailable");

        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var serving = ServePages(server, stop.Token);
        string profile = Path.Combine(Path.GetTempPath(), "murmur-browser-probe-" + Guid.NewGuid().ToString("N"));
        using var browser = Launch(exe!, profile, $"http://127.0.0.1:{port}/first", "--new-window");
        try
        {
            Assert.True(await WaitForPage(processName, "/first"), "normal window did not match its active page");
            Assert.True(FocusProbe.ReadBrowserPage(FocusProbe.Capture(false), out bool addressBar) is not null, "normal window read vanished");
            output.WriteLine(addressBar ? "Normal: address ValuePattern" : "Normal: document value");
            output.WriteLine($"Foreground DPI: {GetDpiForWindow(GetForegroundWindow())}");
            var pageFocus = FocusProbe.Capture(readText: false);

            Press(0x4c, control: true); // Ctrl+L
            Assert.True(SpinWait.SpinUntil(() => FocusProbe.Capture(false) is { } bar && bar.ProcessName == processName &&
                pageFocus is not null && !pageFocus.SameElement(bar), TimeSpan.FromSeconds(3)), "address bar did not take focus");
            Assert.True(FocusProbe.ReadBrowserPage(FocusProbe.Capture(false)) is null, "address bar focus must not read a page");
            Assert.True(FocusProbe.ReadBrowserPage(pageFocus) is null, "stale page focus must not read a page");

            Press(0x1b); // Escape
            using var tab = Launch(exe!, profile, $"http://127.0.0.1:{port}/tab");
            Assert.True(await WaitForPage(processName, "/tab"), "new tab did not match its active page");
            Press(0x09, control: true); // Ctrl+Tab returns to the first tab.
            Assert.True(await WaitForPage(processName, "/first"), "tab switch did not match the first page");

            Press(0x7a); // F11
            await Task.Delay(500);
            Assert.True(await WaitForPage(processName, "/first"), "full screen did not match document with toolbar hidden");
            Press(0x7a);

            using var second = Launch(exe!, profile, $"http://127.0.0.1:{port}/second", "--new-window");
            Assert.True(await WaitForPage(processName, "/second"), "second window did not match its own page");

            using var embedded = Launch(exe!, profile, $"http://127.0.0.1:{port}/embedded", "--new-window");
            Assert.True(await WaitForPage(processName, "/embedded"), "focused iframe did not match its top-level page");

            using var privateWindow = Launch(exe!, profile, $"http://127.0.0.1:{port}/private", chrome ? "--incognito" : "--inprivate", "--new-window");
            Assert.True(await WaitForPage(processName, "/private"), "private window did not match its own page");
        }
        finally
        {
            if (!browser.HasExited)
            {
                browser.Kill(entireProcessTree: true);
                await browser.WaitForExitAsync();
            }
            await stop.CancelAsync();
            await serving;
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }

    static Process Launch(string edge, string profile, string page, params string[] flags)
    {
        var info = new ProcessStartInfo(edge) { UseShellExecute = false };
        foreach (var arg in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-extensions" }.Concat(flags).Append(page))
            info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    static async Task<bool> WaitForPage(string processName, string path)
    {
        var wait = Stopwatch.StartNew();
        var app = new WritingMode { Apps = [processName] };
        var site = new WritingMode { Websites = ["127.0.0.1" + path] };
        while (wait.Elapsed.TotalSeconds < 18)
        {
            if (FocusProbe.Capture(false) is { } focus && focus.ProcessName == processName &&
                ReferenceEquals(WritingModes.Match([app, site], processName, FocusProbe.ReadBrowserPage(focus)), site)) return true;
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
                using var client = await server.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();
                using var request = new StreamReader(stream, leaveOpen: true);
                string? requestLine = await request.ReadLineAsync(ct);
                while (await request.ReadLineAsync(ct) is { Length: > 0 }) { }
                const string prefix = "<!doctype html><title>Murmur website probe</title>";
                var html = prefix + (requestLine?.StartsWith("GET /embedded ", StringComparison.Ordinal) == true
                    ? """<iframe src="/frame" onload="this.contentWindow.focus(); this.contentDocument.querySelector('input').focus()"></iframe>"""
                    : "<label>Field <input autofocus></label>");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n{html}"), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
    }
}

[CollectionDefinition("Browser desktop", DisableParallelization = true)]
public sealed class BrowserDesktopCollection;
