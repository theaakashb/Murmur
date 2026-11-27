using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;

namespace Murmur.App;

/// <summary>The Meetings screen the first time it opens (app/MeetingsUI.swift MeetingsSetup): the languages meetings are held in (a copy of the dictation languages to start),
/// the on-device model they need, and how notes are written. Continue saves the languages, which ends setup.</summary>
internal sealed class MeetingsSetup
{
    readonly AppServices s;
    readonly Controller controller;
    readonly Func<XamlRoot?> root;
    readonly Action done;
    readonly ContentControl modelHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
    readonly Button continueButton;
    readonly Segmented notes;
    static readonly MeetingNotesRoute[] NoteRoutes = [MeetingNotesRoute.None, MeetingNotesRoute.LocalModel];
    List<string> languages;
    ModelRow? row;
    LocalSpeechModel shown;

    public FrameworkElement View { get; }

    public MeetingsSetup(AppServices services, Controller controller, Func<XamlRoot?> root, Action done)
    {
        s = services; this.controller = controller; this.root = root; this.done = done;
        languages = [.. Prefs.DictationLanguages.Read(s.Settings.Get(Prefs.DictationLanguages.Key))];
        var heading = new TextBlock { Text = "Set up meetings", Style = MurmurStyle.Style("MurmurHeading") };
        heading.FontSize = 28;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        var lede = MurmurStyle.Text("Murmur transcribes meetings on this PC and can write notes when you stop. Meetings can use different languages from dictation, for example when you speak one language and the other person another.", "MurmurDescription");
        var picker = new DictationLanguagePicker(() => languages, v => { languages = [.. v]; ShowModel(); }, () => true, "What languages do you take meetings in?");

        var current = MeetingNotesRoutes.Load(s.Settings) ?? MeetingNotesRoute.None;
        notes = new Segmented("Meeting notes", [.. NoteRoutes.Select(r => r.Label())], Math.Max(0, Array.IndexOf(NoteRoutes, current)), _ => { }, stretch: false);
        var advanced = MurmurStyle.Text("A Custom Provider for both is under Advanced in Settings → Meetings.", "MurmurCaption");

        continueButton = MurmurStyle.Pill("Continue", Finish, MurmurStyle.PillKind.Filled, "Continue", height: 34);
        var buttons = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { continueButton } };
        View = MurmurStyle.Named(new ScrollViewer
        {
            Content = new StackPanel
            {
                Padding = new Thickness(40, 32, 40, 32), Spacing = 20, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    heading, lede, picker.Panel,
                    new StackPanel { Spacing = 6, Children = { Section("Meeting transcription"), modelHost } },
                    new StackPanel { Spacing = 6, Children = { Section("Meeting notes"), notes } },
                    advanced, buttons,
                }
            }
        }, "Set up meetings");
        ShowModel();
    }

    static TextBlock Section(string text)
    {
        var block = MurmurStyle.Text(text, "MurmurSettingLabel");
        block.FontSize = 16; block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return block;
    }

    /// <summary>The model the chosen languages need, with its Download / Cancel row.</summary>
    void ShowModel()
    {
        continueButton.IsEnabled = languages.Count > 0;
        var model = MeetingLanguages.Model(languages);
        if (row is not null && model == shown) return;
        row?.Detach();
        shown = model;
        row = new ModelRow(s, controller.SpeechModelSlot(model), controller, root, go => { s.Local.Release(); go(); }, () => { }, speechModel: () => model);
        modelHost.Content = MurmurStyle.Card(row.Panel);
    }

    void Finish()
    {
        if (languages.Count == 0) return;
        // The setup offers None or on this PC only: an existing Custom Provider notes route is kept.
        if (MeetingNotesRoutes.Load(s.Settings) != MeetingNotesRoute.CustomProvider) s.Settings.Set("meetingNotesRoute", NoteRoutes[Math.Max(0, notes.SelectedIndex)].SettingValue());
        row?.Detach();
        s.Settings.Set(MeetingLanguages.Setting, languages);
        done();
    }
}
