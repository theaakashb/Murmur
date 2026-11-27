using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>Writing's Modes, Dictionary and Cues tabs, over the ported Core stores (settings "writingModes", dictionary.json, cues.json).</summary>
internal sealed partial class SettingsView
{
    StackPanel modesPage = null!, dictionaryPage = null!, cuesPage = null!;
    Guid? editingMode, editingEntry, editingCue;

    // ---- Voice edit shortcut (app/VoiceEditSettings.swift VoiceEditShortcutSettings) ----

    static string EditLabel(string spec)
    {
        var parts = spec.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var vk)) return "";
        int mask = parts.Length == 3 && int.TryParse(parts[2], out var m) ? m : 0;
        var names = new List<string>();
        if ((mask & 1) != 0) names.Add("Ctrl");
        if ((mask & 2) != 0) names.Add("Shift");
        if ((mask & 4) != 0) names.Add("Alt");
        if ((mask & 8) != 0) names.Add("Win");
        names.Add(Native.KeyName(vk));
        return string.Join("+", names);
    }

    void BuildVoiceEdit()
    {
        bool separate = s.Settings.Get(Prefs.VoiceEditShortcut) != EditTrigger.SameKey;
        var recorded = MurmurStyle.Text(separate ? EditLabel(s.Settings.Get(Prefs.VoiceEditShortcut)) : "", "MurmurCaption");
        var record = MurmurStyle.Named(new Button { Content = "Record key" }, "Record voice edit key");
        var detail = new StackPanel { Spacing = 8, Visibility = separate ? Visibility.Visible : Visibility.Collapsed };
        var picker = Picker("Voice edit shortcut", ["Same as dictation key, when text is selected", "A separate shortcut"], separate ? 1 : 0, i =>
        {
            detail.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (i == 0) { s.Settings.Set(Prefs.VoiceEditShortcut, EditTrigger.SameKey); recorded.Text = ""; controller.ReloadVoiceEditShortcut(); }
        });
        picker.Width = 340;
        Row("Voice edit shortcut", null, picker);
        record.Click += async (_, _) =>
        {
            record.IsEnabled = false;
            recorded.Text = "Press a key (Esc cancels)…";
            var vk = await controller.RecordNextKey();
            string? refusal = null;
            if (vk != 0)
            {
                // Modifiers still held when the key arrives become part of the shortcut: Ctrl=1 Shift=2 Alt=4 Win=8.
                int mask = (Held(Windows.System.VirtualKey.Control) ? 1 : 0) | (Held(Windows.System.VirtualKey.Shift) ? 2 : 0) | (Held(Windows.System.VirtualKey.Menu) ? 4 : 0)
                    | (Held(Windows.System.VirtualKey.LeftWindows) || Held(Windows.System.VirtualKey.RightWindows) ? 8 : 0);
                var spec = mask == 0 ? $"custom:{vk}" : $"custom:{vk}:{mask}";
                int dictation = ShortcutSpec.KeyOf(s.Settings.Get(Prefs.Shortcut));
                if (vk == dictation) refusal = "That is your dictation shortcut. Pick another key.";
                else if (VoiceEditBinding.Parse(spec, dictation) is null) refusal = "That key can't start voice edit. Pick another key.";
                else { s.Settings.Set(Prefs.VoiceEditShortcut, spec); controller.ReloadVoiceEditShortcut(); }
            }
            var current = s.Settings.Get(Prefs.VoiceEditShortcut);
            recorded.Text = refusal ?? (current == EditTrigger.SameKey ? "" : EditLabel(current));
            record.IsEnabled = true;
        };
        detail.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { recorded, record } });
        detail.Children.Add(MurmurStyle.Text("A key that also edits text, such as Ctrl+E, is used only when text is selected. Otherwise it works as usual.", "MurmurCaption"));
        page.Children.Add(detail);
        page.Children.Add(MurmurStyle.Text("Select text, press the shortcut and say how to change it. To replace selected text by dictation, delete it first, or pick a separate voice edit shortcut. Works with a Custom Provider or Murmur Cloud; pasting the result makes rich text plain.", "MurmurCaption"));
    }

    static bool Held(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ---- shared bits ----

    static Button Btn(string text, Action click, string? name = null, bool plain = false)
    {
        var b = MurmurStyle.Named(new Button { Content = text, MinHeight = 30, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(5) }, name ?? text);
        if (plain) { b.BorderThickness = new Thickness(0); b.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent); }
        b.Click += (_, _) => click();
        return b;
    }

    static TextBox Box(string header, string value, string placeholder, Action<string> changed, bool multiline = false, int max = 0)
    {
        var box = MurmurStyle.Named(new TextBox { Header = header, Text = value, PlaceholderText = placeholder, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 120 : 0, MaxLength = max }, header);
        box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    TextBlock Caption(string text, double top = 6) { var t = MurmurStyle.Text(text, "MurmurCaption"); t.Margin = new Thickness(0, top, 0, 0); return t; }

    void Render(StackPanel target, Action body)
    {
        var saved = page;
        page = target;
        page.Children.Clear();
        try { body(); } finally { page = saved; }
    }

    static List<string> Split(string text) =>
        [.. text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    // ---- Modes ----

    void BuildModes() { modesPage = page; Render(modesPage, RenderModes); }

    List<WritingMode>? LoadModes()
    {
        try { return WritingModes.Load(s.Settings); } catch (Exception) { return null; }
    }

    void ChangeModes(Func<List<WritingMode>, List<WritingMode>> change, bool redraw = true)
    {
        if (LoadModes() is not { } modes) return;
        try { WritingModes.Save(change(modes), s.Settings); } catch (Exception) { return; }
        if (redraw) Render(modesPage, RenderModes);
    }

    void ChangeMode(Guid id, Func<WritingMode, WritingMode> change, bool redraw = false) =>
        ChangeModes(list => [.. list.Select(m => m.Id == id ? change(m) : m)], redraw);

    static string ModeSummary(WritingMode m)
    {
        List<string> parts = [];
        if (m.Triggers.Count > 0) parts.Add("Said: " + string.Join(", ", m.Triggers));
        if (m.Websites.Count > 0) parts.Add(string.Join(", ", m.Websites));
        if (m.Apps.Count > 0) parts.Add(string.Join(", ", m.Apps));
        return parts.Count == 0 ? "Matches nothing yet" : string.Join(" · ", parts);
    }

    void RenderModes()
    {
        Heading("Writing modes");
        page.Children.Add(Caption("A mode changes how Murmur writes in chosen apps or websites, or when you say a trigger phrase."));
        if (LoadModes() is not { } modes) { Note("Your modes could not be read. They are kept aside and not overwritten."); return; }
        foreach (var m in modes)
        {
            var mode = m;
            var name = MurmurStyle.Text(mode.Name.Length == 0 ? "Untitled mode" : mode.Name, "MurmurSettingLabel");
            name.FontSize = 16; name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { name, MurmurStyle.Text((mode.Enabled ? "" : "Off · ") + ModeSummary(mode), "MurmurCaption") } };
            bool open = editingMode == mode.Id;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    Btn(open ? "Done" : "Edit", () => { editingMode = open ? null : mode.Id; Render(modesPage, RenderModes); }, (open ? "Done editing " : "Edit ") + mode.Name),
                    Btn("Delete", () => { if (editingMode == mode.Id) editingMode = null; ChangeModes(l => [.. l.Where(x => x.Id != mode.Id)]); }, "Delete " + mode.Name, plain: true)
                }
            };
            var head = new Grid { Padding = new Thickness(0, 12, 0, 12), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { labels, buttons } };
            Grid.SetColumn(buttons, 1);
            page.Children.Add(head);
            if (open) page.Children.Add(ModeEditor(mode));
            page.Children.Add(new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") });
        }
        var add = Btn("Add Mode", () => { var fresh = new WritingMode { Name = "New mode" }; editingMode = fresh.Id; ChangeModes(l => [.. l, fresh]); });
        add.Margin = new Thickness(0, 16, 0, 0);
        page.Children.Add(add);
    }

    FrameworkElement ModeEditor(WritingMode mode)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 16) };
        var id = mode.Id;
        void Label(string text) { var l = MurmurStyle.Text(text, "MurmurSettingLabel"); l.FontSize = 14; l.FontWeight = Microsoft.UI.Text.FontWeights.Medium; l.Margin = new Thickness(0, 14, 0, 0); panel.Children.Add(l); }
        panel.Children.Add(Box("Mode name", mode.Name, "Mode name", v => ChangeMode(id, x => x with { Name = v.Trim() })));
        var on = MurmurStyle.Named(new ToggleSwitch { IsOn = mode.Enabled, OnContent = null, OffContent = null, MinWidth = 0 }, "Mode on");
        on.Toggled += (_, _) => ChangeMode(id, x => x with { Enabled = on.IsOn });
        var onRow = new Grid { MinHeight = 48, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { MurmurStyle.Text("Mode on", "MurmurSettingLabel"), on } };
        Grid.SetColumn(on, 1); onRow.Children[0].SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.Children.Add(onRow);

        Label("Custom instructions");
        panel.Children.Add(Box("Custom instructions", mode.Instructions, "", v => ChangeMode(id, x => x with { Instructions = SwiftText.Prefix(v, WritingModeLimits.Instructions) }), multiline: true, max: WritingModeLimits.Instructions));
        panel.Children.Add(Caption(mode.Transform is null ? "Sent only to AI cleanup routes. Cleanup still preserves what you said." : "These are the rewriting instructions sent to AI cleanup. Edit them to make this mode your own. Murmur checks the result, but rewrites can still make mistakes."));

        Label("Apps");
        foreach (var app in mode.Apps)
        {
            var a = app;
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { MurmurStyle.Text(a, "MurmurSettingLabel"), Btn("Remove", () => { ChangeMode(id, x => x with { Apps = (List<string>)[.. x.Apps.Where(y => y != a)] }, true); }, "Remove " + a, plain: true) } };
            Grid.SetColumn(row.Children[1] as FrameworkElement, 1);
            panel.Children.Add(row);
        }
        var addApp = new Button { Content = "Add App", MinHeight = 30, HorizontalAlignment = HorizontalAlignment.Left };
        var menu = new MenuFlyout();
        List<string> running = [];
        try { running = [.. System.Diagnostics.Process.GetProcesses().Where(p => p.MainWindowHandle != 0).Select(p => p.ProcessName.ToLowerInvariant()).Distinct().Order()]; } catch (Exception) { /* the list stays empty */ }
        foreach (var app in running.Where(r => !mode.Apps.Contains(r)))
        {
            var item = new MenuFlyoutItem { Text = app };
            item.Click += (_, _) => ChangeMode(id, x => x with { Apps = (List<string>)[.. x.Apps, app] }, true);
            menu.Items.Add(item);
        }
        addApp.Flyout = menu;
        panel.Children.Add(addApp);

        Label("Websites");
        panel.Children.Add(Box("Websites", string.Join(", ", mode.Websites), "github.com, docs.google.com/document/*, *.slack.com", v => ChangeMode(id, x => x with { Websites = Split(v) })));
        panel.Children.Add(Caption("Matches the page open in the browser when recording starts. A site includes its subdomains; add a path to match only that part. The address is read through Windows accessibility, only while a mode lists a website, and is never saved or sent. A browser that does not show its page matches by app only."));

        Label("Trigger words");
        panel.Children.Add(Box("Trigger words", string.Join(", ", mode.Triggers), "email mode", v => ChangeMode(id, x => x with { Triggers = Split(v) })));
        panel.Children.Add(Caption(mode.Transform is null ? "Say one at the start or end of a dictation to use this mode; it is removed from the text. Pick a phrase you would not otherwise say there."
            : "Say one at the very start of a dictation to use this mode; it is removed from the text. This built-in mode rewrites the text on AI cleanup routes; the custom instructions above control its rewrite."));

        Label("Cleanup");
        var route = Routes.Cleanup;
        var cloud = route.RunsModelLevels();
        var style = mode.Options is { } saved ? 1 + (int)CleanupStyles.Current(route, saved) : 0;
        var cleanup = new Segmented("Mode cleanup", ["Same as Cleanup", "Faithful", "Natural", "Polish", "Professional"], style,
            i => ChangeMode(id, x => x with { Options = ModeOptions(x, i) }, true), cloud: cloud ? null : [3, 4], fontSize: 13);
        cleanup.SetEnabled(3, cloud);
        cleanup.SetEnabled(4, cloud);
        panel.Children.Add(cleanup);
        if (mode.Options is { } options && route.EffectiveOptions(options) is { AnyEnabled: true } opts)
        {
            (string, string, Func<CleanupOptions, bool>, Func<CleanupOptions, bool, CleanupOptions>)[] flags =
            [
                ("Remove filler words", "Takes out “um”, “uh” and similar.", x => x.Fillers, (x, v) => x with { Fillers = v }),
                ("Apply spoken corrections", "Uses your final wording for clear corrections. Ambiguous corrections stay.", x => x.Corrections, (x, v) => x with { Corrections = v }),
                ("Format spoken lists", "Turns “one, two, three” into a numbered list.", x => x.Lists, (x, v) => x with { Lists = v }),
                ("Fix punctuation and capitalization", "Adds commas, periods and capitals.", x => x.Punctuation, (x, v) => x with { Punctuation = v }),
                ("Spoken commands and numbers", "“comma”, “new paragraph”, numbers as digits.", x => x.Spoken, (x, v) => x with { Spoken = v }),
            ];
            foreach (var (title, help, get, set) in flags)
            {
                var t = MurmurStyle.Named(new ToggleSwitch { IsOn = get(opts), OnContent = null, OffContent = null, MinWidth = 0 }, title);
                t.Toggled += (_, _) => ChangeMode(id, x => x with { Options = x.Options is { } cur ? set(cur, t.IsOn) : x.Options });
                var row = new Grid { MinHeight = 48, ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
                row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { MurmurStyle.Text(title, "MurmurSettingLabel"), Help(help) } });
                Grid.SetColumn(t, 1); row.Children.Add(t);
                panel.Children.Add(row);
            }
        }

        Label("Tone");
        Tone[] tones = [Tone.Formal, Tone.Casual, Tone.VeryCasual, Tone.Excited];
        var toneNote = Caption("");
        string ToneText(Tone t) => t switch { Tone.Formal => "Text stays as cleanup wrote it.", Tone.Casual => "Drops commas and the final period. Commas inside numbers stay.", Tone.VeryCasual => "Casual, and sentences start in lowercase. “I” and words like “iPhone” keep their capitals.", _ => "The final period becomes “!”." };
        toneNote.Text = ToneText(mode.Tone);
        panel.Children.Add(new Segmented("Mode tone", [.. tones.Select(t => t.DisplayName())], Array.IndexOf(tones, mode.Tone), i => { ChangeMode(id, x => x with { Tone = tones[i] }); toneNote.Text = ToneText(tones[i]); }, fontSize: 13));
        panel.Children.Add(toneNote);
        panel.Children.Add(Caption("Applied by rules after cleanup, on every route. Never applied to an email laid out from “write an email to…”.", 2));

        Label("Screen context");
        var context = new Segmented("Mode screen context", ["Same as Cleanup", "On", "Off"], !cloud ? 2 : mode.ScreenContext is null ? 0 : mode.ScreenContext == true ? 1 : 2,
            i => ChangeMode(id, x => x with { ScreenContext = i == 0 ? null : i == 1 }), fontSize: 13);
        for (int i = 0; i < 3; i++) context.SetEnabled(i, cloud);
        panel.Children.Add(context);
        return panel;
    }

    // As the Mac's modeStyle: Faithful is every rule off; a model style starts from the global rules (or the defaults when those are all off).
    CleanupOptions? ModeOptions(WritingMode mode, int index)
    {
        if (index == 0) return null;
        if (index == 1) return CleanupOptions.Faithful;
        var current = mode.Options is { AnyEnabled: true } c ? c
            : new CleanupOptions(s.Settings.Get(Prefs.CleanupFillers), s.Settings.Get(Prefs.CleanupCorrections), s.Settings.Get(Prefs.CleanupLists), s.Settings.Get(Prefs.CleanupPunctuation), spoken: s.Settings.Get(Prefs.CleanupSpoken));
        if (!current.AnyEnabled) current = CleanupOptions.Standard;
        return current with { Polish = index >= 3, Professional = index == 4 };
    }

    // ---- Dictionary ----

    void BuildDictionary() { dictionaryPage = page; Render(dictionaryPage, RenderDictionary); }

    void RenderDictionary()
    {
        Heading("Dictionary");
        page.Children.Add(Caption("Words and names Murmur should spell your way. “Heard as” lists the ways it may mishear them."));
        if (s.Processing is not { } processing) { Note("The dictionary could not be read. Dictations are held in History until it is repaired."); return; }
        var message = MurmurStyle.Text("", "MurmurCaption");
        var preferred = Box("Preferred spelling", "", "Preferred spelling", _ => { });
        var heard = Box("Heard as (optional)", "", "Heard as (optional)", _ => { });
        var add = Btn("Add", () =>
        {
            if (string.IsNullOrWhiteSpace(preferred.Text)) return;
            try { processing.AddEntry(preferred.Text, heard.Text); Render(dictionaryPage, RenderDictionary); }
            catch (StoreError e) { message.Text = e.Message; }
        }, "Add dictionary entry");
        add.VerticalAlignment = VerticalAlignment.Bottom;
        var form = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 12, 0, 8), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { preferred, heard, add } };
        Grid.SetColumn(heard, 1); Grid.SetColumn(add, 2);
        page.Children.Add(form);
        page.Children.Add(message);
        page.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 0), Style = MurmurStyle.Style("MurmurRuleLine") });
        foreach (var entry in processing.Dictionary.Entries.OrderBy(e => e.Preferred, StringComparer.CurrentCultureIgnoreCase))
        {
            var e = entry;
            if (editingEntry == e.Id)
            {
                var p = Box("Preferred spelling", e.Preferred, "", _ => { });
                var v = Box("Heard as (optional)", string.Join(", ", e.Variants), "", _ => { });
                var save = Btn("Save", () => { try { processing.UpdateEntry(e.Id, p.Text, v.Text, e.Enabled); editingEntry = null; Render(dictionaryPage, RenderDictionary); } catch (StoreError ex) { message.Text = ex.Message; } });
                var cancel = Btn("Cancel", () => { editingEntry = null; Render(dictionaryPage, RenderDictionary); }, plain: true);
                var edit = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 10, 0, 10), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { p, v, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom, Children = { save, cancel } } } };
                Grid.SetColumn(v, 1); Grid.SetColumn(edit.Children[2] as FrameworkElement, 2);
                page.Children.Add(edit);
            }
            else
            {
                var name = MurmurStyle.Text(e.Preferred, "MurmurSettingLabel"); name.FontSize = 15;
                var detail = (e.Variants.Count > 0 ? "Heard as: " + string.Join(", ", e.Variants) : "No alternate spellings") + (e.Source == EntrySource.Learned ? " · Learned" : "");
                var on = MurmurStyle.Named(new ToggleSwitch { IsOn = e.Enabled, OnContent = null, OffContent = null, MinWidth = 0 }, "Use " + e.Preferred);
                on.Toggled += (_, _) => { try { processing.UpdateEntry(e.Id, e.Preferred, string.Join(", ", e.Variants), on.IsOn); } catch (StoreError ex) { message.Text = ex.Message; } };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { on, Btn("Edit", () => { editingEntry = e.Id; Render(dictionaryPage, RenderDictionary); }, "Edit " + e.Preferred, plain: true), Btn("Delete", () => { try { processing.DeleteEntry(e.Id); Render(dictionaryPage, RenderDictionary); } catch (StoreError ex) { message.Text = ex.Message; } }, "Delete " + e.Preferred, plain: true) } };
                var row = new Grid { MinHeight = 64, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, MurmurStyle.Text(detail, "MurmurCaption") } }, buttons } };
                Grid.SetColumn(buttons, 1);
                page.Children.Add(row);
            }
            page.Children.Add(new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") });
        }

        IReadOnlyList<DictionarySuggestion> suggestions = [];
        try
        {
            suggestions = DictionarySuggestions.Find((List<(string, string)>)[.. s.History.Entries().Where(h => h.EditOriginal == null).Select(h => (h.RawText, h.FinalText))],
                processing.Dictionary.Entries, new HashSet<string>(s.Settings.Get(Prefs.DismissedSuggestions)), Lexicon.English.IsCommonWord);
        }
        catch (Exception) { /* History unreadable: no suggestions */ }
        if (suggestions.Count > 0)
        {
            Heading("Suggested");
            page.Children.Add(Caption("Words Murmur keeps hearing that aren't in your dictionary. Fix the spelling if needed, then add it.", 0));
            page.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 0), Style = MurmurStyle.Style("MurmurRuleLine") });
            foreach (var item in suggestions)
            {
                var it = item;
                var spelling = Box("Spelling", it.Preferred, "", _ => { });
                var detail = MurmurStyle.Text($"Heard as {string.Join(", ", it.Heard)} · {it.Dictations} dictations", "MurmurCaption");
                var addSuggestion = Btn("Add", () =>
                {
                    var word = spelling.Text.Trim();
                    if (word.Length == 0) return;
                    try
                    {
                        var heardForms = it.Heard.Where(h => TermText.Fold(h) != TermText.Fold(word)).ToList();
                        if (processing.Dictionary.Entries.FirstOrDefault(e => TermText.Fold(e.Preferred) == TermText.Fold(word)) is { } existing)
                            processing.UpdateEntry(existing.Id, existing.Preferred, string.Join(", ", existing.Variants.Concat(heardForms.Where(h => !existing.Variants.Any(v => TermText.Fold(v) == TermText.Fold(h))))), existing.Enabled);
                        else processing.AddEntry(word, string.Join(", ", heardForms));
                        Render(dictionaryPage, RenderDictionary);
                    }
                    catch (StoreError ex) { message.Text = ex.Message; }
                }, "Add " + it.Preferred);
                var dismiss = Btn("Dismiss", () => { s.Settings.Set(Prefs.DismissedSuggestions, (List<string>)[.. s.Settings.Get(Prefs.DismissedSuggestions), .. it.Heard.Select(TermText.Fold)]); Render(dictionaryPage, RenderDictionary); }, "Dismiss " + it.Heard[0], plain: true);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { addSuggestion, dismiss } };
                var row = new Grid { MinHeight = 72, ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { spelling, detail } }, buttons } };
                Grid.SetColumn(buttons, 1);
                page.Children.Add(row);
                page.Children.Add(new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") });
            }
        }

        var blocked = processing.Ledger.Blocked;
        if (blocked.Count > 0)
        {
            Heading("Not learned");
            page.Children.Add(Caption("Skipped because your fixes conflicted or were undone.", 0));
            page.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 0), Style = MurmurStyle.Style("MurmurRuleLine") });
            foreach (var item in blocked)
            {
                var (word, why) = item switch
                {
                    CorrectionLedger.BlockedItem.Heard h => (h.Source, "Fixes for this heard form are not learned"),
                    CorrectionLedger.BlockedItem.Spelling sp => (sp.Target, "This spelling is not learned"),
                    _ => ("", "")
                };
                var name = MurmurStyle.Text(word, "MurmurSettingLabel"); name.FontSize = 16; name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                var unblock = Btn("Unblock", () => { processing.Ledger.Unblock(item); Render(dictionaryPage, RenderDictionary); }, "Unblock " + word);
                var row = new Grid { MinHeight = 64, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, MurmurStyle.Text(why, "MurmurCaption") } }, unblock } };
                Grid.SetColumn(unblock, 1); unblock.VerticalAlignment = VerticalAlignment.Center;
                page.Children.Add(row);
                page.Children.Add(new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") });
            }
        }

        var reset = Btn("Reset Learned…", () => _ = ResetLearned(processing, message), "Reset learned entries");
        reset.IsEnabled = processing.Dictionary.Entries.Any(e => e.Source == EntrySource.Learned) || blocked.Count > 0;
        reset.Margin = new Thickness(0, 16, 0, 0); reset.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(Caption("“Heard as” forms are always replaced. With cleanup on, sound-alikes get fixed too. Edits save immediately.", 16));
        page.Children.Add(reset);
    }

    async Task ResetLearned(ProcessingCoordinator processing, TextBlock message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root(), Title = "Reset learned entries?", Content = new TextBlock { Text = "Removes the words Murmur learned from your corrections and what it has seen so far. Entries you added yourself stay.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Reset Learned", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await MurmurStyle.Named(dialog, "Reset learned entries").ShowAsync() != ContentDialogResult.Primary) return;
        try { processing.ResetLearned(); Render(dictionaryPage, RenderDictionary); }
        catch (Exception) { message.Text = "Could not reset learned entries."; }
    }

    // ---- Cues ----

    void BuildCues() { cuesPage = page; Render(cuesPage, RenderCues); }

    void RenderCues()
    {
        Heading("Voice cues");
        page.Children.Add(Caption("Say a short phrase and Murmur types the text you saved for it."));
        if (s.Processing?.Cues is not { } cues) { Note("Cues could not be read."); return; }
        if (cues.Unreadable) { Note("Your cues could not be read. They are kept as they are and not overwritten."); return; }
        var message = MurmurStyle.Text("", "MurmurCaption");
        var phrase = Box("Phrase", "", "my email", _ => { });
        var text = Box("Text to type", "", "name@example.com", _ => { }, multiline: true, max: VoiceCueStore.MaxTextLength);
        text.MinHeight = 72;
        var add = Btn("Add", () =>
        {
            if (string.IsNullOrWhiteSpace(phrase.Text) || string.IsNullOrWhiteSpace(text.Text)) return;
            try { cues.Add(phrase.Text, text.Text); Render(cuesPage, RenderCues); }
            catch (StoreError e) { message.Text = e.Message; }
        }, "Add voice cue");
        var form = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 8), Children = { phrase, text, add, message } };
        page.Children.Add(form);
        page.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 0), Style = MurmurStyle.Style("MurmurRuleLine") });
        foreach (var cue in cues.Cues.OrderBy(c => c.Phrase, StringComparer.CurrentCultureIgnoreCase))
        {
            var c = cue;
            if (editingCue == c.Id)
            {
                var p = Box("Phrase", c.Phrase, "", _ => { });
                var t = Box("Text to type", c.Text, "", _ => { }, multiline: true, max: VoiceCueStore.MaxTextLength);
                t.MinHeight = 72;
                var save = Btn("Save", () => { try { cues.Update(c.Id, p.Text, t.Text); editingCue = null; Render(cuesPage, RenderCues); } catch (StoreError ex) { message.Text = ex.Message; } });
                var cancel = Btn("Cancel", () => { editingCue = null; Render(cuesPage, RenderCues); }, plain: true);
                page.Children.Add(new StackPanel { Spacing = 8, Padding = new Thickness(0, 10, 0, 10), Children = { p, t, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { save, cancel } } } });
            }
            else
            {
                var name = MurmurStyle.Text(c.Phrase, "MurmurSettingLabel"); name.FontSize = 15;
                var preview = MurmurStyle.Text(c.Text, "MurmurCaption"); preview.MaxLines = 2; preview.TextTrimming = TextTrimming.CharacterEllipsis;
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { Btn("Edit", () => { editingCue = c.Id; Render(cuesPage, RenderCues); }, "Edit cue " + c.Phrase, plain: true), Btn("Delete", () => { try { cues.Delete(c.Id); Render(cuesPage, RenderCues); } catch (StoreError ex) { message.Text = ex.Message; } }, "Delete cue " + c.Phrase, plain: true) } };
                var row = new Grid { MinHeight = 64, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, preview } }, buttons } };
                Grid.SetColumn(buttons, 1);
                page.Children.Add(row);
            }
            page.Children.Add(new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") });
        }
    }
}
