using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using Murmur.Core;

namespace Murmur.App;

/// <summary>The searchable multi-select dictation language picker (app/DictationLanguagePicker.swift). Names come from .NET's CultureInfo (the UI culture's name, then the language's own name on the right).</summary>
internal sealed class DictationLanguagePicker
{
    const double RowHeight = 32;
    readonly CultureInfo culture = CultureInfo.CurrentUICulture;
    readonly Func<IReadOnlyList<string>> get;
    readonly Action<IReadOnlyList<string>> set;
    readonly Func<bool> onDevice;
    // A RichTextBlock: a TextBlock cannot hold the flag images (InlineUIContainer throws ArgumentException in WinUI 3 and Settings failed to open).
    readonly RichTextBlock selected = new() { IsTextSelectionEnabled = false };
    readonly TextBox search = MurmurStyle.Named(new TextBox { PlaceholderText = "Search languages" }, "Search languages");
    readonly StackPanel list = new();
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock none = MurmurStyle.Text("No matching languages");
    readonly TextBlock speedNote = MurmurStyle.Text("Your selected languages need a larger speech model. Transcription may take longer on this PC.", "MurmurCaption");
    public StackPanel Panel { get; } = new() { Spacing = 10 };
    bool enabled = true;
    /// <summary>StackPanel has no IsEnabled in WinUI 3, so the search box and rows are disabled directly.</summary>
    public bool IsEnabled { get => enabled; set { if (enabled == value) return; enabled = value; search.IsEnabled = value; Rebuild(); } }

    /// <param name="get">The saved codes, in the order chosen.</param>
    /// <param name="set">Saves the new selection.</param>
    /// <param name="onDevice">Whether dictation runs on this PC (the larger-model note only applies then).</param>
    public DictationLanguagePicker(Func<IReadOnlyList<string>> get, Action<IReadOnlyList<string>> set, Func<bool> onDevice)
    {
        this.get = get; this.set = set; this.onDevice = onDevice;
        var title = MurmurStyle.Text("What languages do you dictate in?", "MurmurSettingLabel");
        title.FontSize = 16; title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        selected.FontSize = 13; selected.TextWrapping = TextWrapping.Wrap;
        search.TextChanged += (_, _) => Rebuild();
        scroll.Content = list;
        none.Visibility = Visibility.Collapsed;
        speedNote.TextWrapping = TextWrapping.Wrap;
        foreach (var e in new UIElement[] { title, selected, search, none, scroll, speedNote }) Panel.Children.Add(e);
        Refresh();
    }

    /// <summary>The language's flag at the Mac's emoji size (13 pt text: about 17 x 13), or the globe the Mac shows when it has no flag. Hidden from accessibility, as on the Mac.</summary>
    static FrameworkElement Flag(string code, Thickness margin)
    {
        FrameworkElement flag;
        if (DictationLanguagePickerModel.FlagRegion(code) is { } region)
            flag = new Image { Width = 17, Height = 13, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Flags/{region}.svg")) { RasterizePixelWidth = 68, RasterizePixelHeight = 52 } };
        else
            flag = new TextBlock { Text = "🌐", FontSize = 13 };
        flag.Margin = margin;
        flag.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(flag, AccessibilityView.Raw);
        return flag;
    }

    string Name(string code)
    {
        try { var n = CultureInfo.GetCultureInfo(code).DisplayName; if (!string.IsNullOrWhiteSpace(n)) return n; }
        catch (CultureNotFoundException) { }
        return code;
    }

    string Native(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).NativeName; }
        catch (CultureNotFoundException) { return Name(code); }
    }

    /// <summary>Re-reads the saved selection (after a settings import or a change elsewhere).</summary>
    public void Refresh()
    {
        var codes = get();
        var line = new Paragraph();
        for (var i = 0; i < codes.Count; i++)
        {
            if (i > 0) line.Inlines.Add(new Run { Text = "  ·  " });
            line.Inlines.Add(new InlineUIContainer { Child = Flag(codes[i], new Thickness(0, 0, 7, -2)) });
            line.Inlines.Add(new Run { Text = Name(codes[i]) });
        }
        selected.Blocks.Clear();
        selected.Blocks.Add(line);
        AutomationProperties.SetName(selected, "Selected: " + string.Join(", ", codes.Select(Name)));
        selected.Visibility = codes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        speedNote.Visibility = onDevice() && DictationLanguages.Create(codes)?.LocalModel == LocalSpeechModel.WhisperTurbo ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    void Rebuild()
    {
        var codes = get();
        var results = DictationLanguagePickerModel.Results(search.Text, DictationLanguages.Matching(search.Text, culture));
        list.Children.Clear();
        none.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        scroll.Visibility = results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        scroll.Height = Math.Min(results.Count, 5) * RowHeight;
        foreach (var code in results)
        {
            var name = Name(code);
            var native = Native(code);
            var line = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            var flag = Flag(code, new Thickness(0));
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1);
            line.Children.Add(flag); line.Children.Add(label);
            if (!string.Equals(native, name, StringComparison.CurrentCultureIgnoreCase))
            {
                var n = MurmurStyle.Text(native, "MurmurCaption"); n.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(n, 2); line.Children.Add(n);
            }
            var box = MurmurStyle.Named(new CheckBox { Content = line, MinHeight = RowHeight, MinWidth = 0, IsChecked = codes.Contains(code), IsEnabled = enabled && !DictationLanguagePickerModel.IsLocked(codes, code), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch }, name);
            var chosen = code;
            box.Click += (_, _) => { set(DictationLanguagePickerModel.Toggle(get(), chosen, box.IsChecked == true)); Refresh(); };
            list.Children.Add(box);
        }
    }
}
