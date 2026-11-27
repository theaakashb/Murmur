using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Murmur.Core;

namespace Murmur.App;

/// <summary>The Mac's meeting sheets (MeetingsUI.swift): the consent sheet before the first meeting and the notes-route question at the first Stop.
/// Each is its own small dialog window, so it shows even when no Murmur window is open (a call offer, the tray).</summary>
internal sealed partial class MeetingSheets(AppServices s)
{
    Window? open;

    const string ConsentTitle = "Before your first meeting notes";
    const string ConsentBody = "Murmur records your microphone and the audio of the call or apps you choose, and transcribes both on this PC. Recording other people may require their consent where you or they are. You are responsible for letting participants know.";
    const string Acknowledgement = "I'll let participants know";

    Window Sheet(string title, int width, FrameworkElement body, out Action close)
    {
        open?.Close();
        var window = new Window { Title = title };
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 16, Children = { body } };
        window.Content = new Grid { Style = MurmurStyle.Style("MurmurWindowRoot"), Children = { panel } };
        window.ExtendsContentIntoTitleBar = false;
        window.AppWindow.SetPresenter(OverlappedPresenter.CreateForDialog());
        window.AppWindow.TitleBar.PreferredTheme = App.CaptionTheme;
        open = window;
        close = () => { try { window.Close(); } catch (Exception) { /* already closed */ } if (open == window) open = null; };
        MurmurStyle.SetInitialSize(window, width, 360);
        return window;
    }

    static TextBlock Heading(string text)
    {
        var t = MurmurStyle.Text(text, "MurmurSettingLabel");
        t.FontSize = 20; t.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        return t;
    }

    static Button Filled(string text, Action click)
    {
        var accent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 111, 240));
        var b = MurmurStyle.Named(new Button { Content = text, BorderThickness = new Thickness(0), Style = MurmurStyle.Style("AccentButtonStyle"), MinHeight = 36, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(18) }, text);
        foreach (var key in new[] { "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed" }) b.Resources[key] = accent;
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The consent sheet; <paramref name="done"/> gets true only after the box is ticked and Start is pressed.</summary>
    public void Consent(MeetingSource source, Action<bool> done)
    {
        var tick = new CheckBox { Content = Acknowledgement, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Action close = () => { };
        bool answered = false;
        void Answer(bool yes) { if (answered) return; answered = true; close(); done(yes); }
        var start = Filled("Start Meeting Notes", () => Answer(true));
        start.IsEnabled = false;
        tick.Checked += (_, _) => start.IsEnabled = true;
        tick.Unchecked += (_, _) => start.IsEnabled = false;
        var cancel = new Button { Content = "Cancel", MinHeight = 36, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        cancel.Click += (_, _) => Answer(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, start } };
        var body = new StackPanel { Spacing = 16, Children = { Heading(ConsentTitle), MurmurStyle.Text(ConsentBody), tick, buttons } };
        var window = Sheet("Start Meeting Notes", 480, body, out close);
        window.Closed += (_, _) => Answer(false);
        window.Activate();
    }

    /// <summary>Asked once, at the first Stop, with None preselected. <paramref name="done"/> gets the chosen route.</summary>
    public void NotesChoice(Action<MeetingNotesRoute> done)
    {
        var route = MeetingNotesRoute.None;
        var routes = RouteSettings.Load(s.Settings);
        var host = Uri.TryCreate(routes.CustomAPIBaseURL, UriKind.Absolute, out var uri) ? uri.Host : null;
        var disclosure = MurmurStyle.Text("");
        var problem = MurmurStyle.Text("", "MurmurCaption");
        void Refresh()
        {
            disclosure.Text = route.Disclosure(host, uri?.IsLoopback == true, null);
            problem.Text = route == MeetingNotesRoute.LocalModel && !s.LocalNotesSlot.Row.IsReady
                ? "Download the meeting notes model in Settings → Meetings first. A Custom Provider is set up under Advanced there." : "";
        }
        Refresh();
        var tabs = new Segmented("Notes route", [MeetingNotesRoute.None.Label(), MeetingNotesRoute.LocalModel.Label()], 0, i => { route = i == 0 ? MeetingNotesRoute.None : MeetingNotesRoute.LocalModel; Refresh(); }, stretch: false);
        Action close = () => { };
        bool answered = false;
        void Answer() { if (answered) return; answered = true; close(); done(route); }
        var body = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Heading("Write notes for your meetings?"),
                MurmurStyle.Text("After Stop, a model can write a title, a summary, key points, decisions and action items from the transcript. Each item cites the lines it rests on, and items the transcript does not support are left out. You can change this in Settings → Meetings."),
                tabs, disclosure, problem,
                new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { Filled("Save", Answer) } }
            }
        };
        var window = Sheet("Meeting notes", 520, body, out close);
        window.Closed += (_, _) => Answer(); // closing the window keeps the preselected None, like the Mac's single-choice sheet
        window.Activate();
    }
}
