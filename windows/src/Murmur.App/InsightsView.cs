using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Murmur.Core;

namespace Murmur.App;

/// <summary>Insights as on the Mac (insightsView in app/HomeView.swift): metric tiles, the 30-day bars, the streak with its five-week calendar, busiest hours and top apps.</summary>
internal sealed class InsightsView(AppServices s, Action openPrivacy)
{
    static readonly System.Globalization.CultureInfo Culture = System.Globalization.CultureInfo.CurrentCulture;

    string KeptFor => s.Settings.Get(Prefs.Retention) switch { HistoryRetention.SixMonths => "six months", HistoryRetention.ThirtyDays => "30 days", _ => "until you delete them" };

    public UIElement Build()
    {
        var panel = MurmurStyle.Page("Insights", "Counted on this PC from the dictations History keeps. Nothing is sent anywhere.");
        Insights result;
        try { result = Insights.Compute(s.History.Entries().Select(InsightsItem.FromHistory), s.Settings.Get(Prefs.HistoryMode) == HistoryMode.On, DateTimeOffset.Now, TimeZoneInfo.Local); }
        catch (Exception) { result = new Insights.Empty(); }
        switch (result)
        {
            case Insights.HistoryOff:
                panel.Children.Add(Notice("History is off", "Insights counts only what History keeps on this PC, so there is nothing to count. Turn History on in Privacy & Storage to see your words, pace and streaks."));
                break;
            case Insights.Stats stats:
                panel.Children.Add(Stats(stats));
                break;
            default:
                panel.Children.Add(Notice("Nothing to count", $"History keeps dictations for {KeptFor}. It has none right now: either you have not dictated yet, or older dictations have expired. Insights counts only what History still has."));
                break;
        }
        return MurmurStyle.Named(new ScrollViewer { Content = panel }, "Insights");
    }

    UIElement Notice(string title, string detail)
    {
        var heading = MurmurStyle.Text(title, "MurmurSettingLabel");
        heading.FontSize = 22; heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return new Border { Margin = new Thickness(0, 28, 0, 0), Padding = new Thickness(24), Style = MurmurStyle.Style("MurmurAccentCardFlat"), Child = new StackPanel { Spacing = 12, Children = { heading, MurmurStyle.Text(detail), MurmurStyle.Pill("Open Privacy & Storage", openPrivacy) } } };
    }

    static UIElement Heading(string title, string detail) =>
        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { MurmurStyle.Text(title.ToUpperInvariant(), "MurmurEyebrow"), MurmurStyle.Info(detail, "About " + title) } };

    static UIElement Metric(string title, string value, string detail)
    {
        var v = MurmurStyle.Text(value, "MurmurSettingLabel");
        v.FontSize = 28; v.FontWeight = Microsoft.UI.Text.FontWeights.Bold; v.TextWrapping = TextWrapping.NoWrap; v.TextTrimming = TextTrimming.CharacterEllipsis;
        return new StackPanel { Padding = new Thickness(0, 12, 12, 0), Children = { new StackPanel { Spacing = 8, Padding = new Thickness(0, 0, 0, 12), Children = { Heading(title, detail), v } }, MurmurStyle.HRule(true) } };
    }

    UIElement Stats(Insights.Stats st)
    {
        string timed = $"Based on {st.TimedDictations} of {st.Dictations} dictations with a recorded duration. Includes pauses.";
        var root = new StackPanel { Spacing = 24, Margin = new Thickness(0, 20, 0, 0) };
        var metrics = new List<UIElement>
        {
            Metric("Words dictated", st.Words.ToString("N0"), "Words in the final transcripts still in History."),
            Metric("Dictations", st.Dictations.ToString("N0"), "Dictations with a nonempty final transcript."),
            Metric("Recording time", st.RecordingSeconds is { } r ? Insights.Duration(r) : "—", timed),
            Metric("Speaking pace", st.WordsPerMinute is { } w ? $"{Math.Round(w)} WPM" : "—", "Five-character words per minute. " + timed),
            Metric("Est. time saved", st.SecondsSaved is { } t ? Insights.Duration(t) : "—", $"Estimated against typing at {Math.Round(Insights.TypingWPM)} WPM; not your measured typing speed. " + timed),
            Metric("Words / dictation", st.AverageWords.ToString("0.0"), "Average final word count per dictation."),
            Metric("Words changed", st.WordsChanged.ToString("N0"), "Differences between raw and final text, including cleanup, dictionary spellings and History edits. Not an accuracy score."),
        };
        root.Children.Add(Adaptive(metrics, 170, 12, 12));

        var cards = new List<UIElement>
        {
            Daily(st), StreakCard(st), Hourly(st), TopApps(st),
        };
        root.Children.Add(Adaptive(cards, 280, 28, 28));
        root.Children.Add(MurmurStyle.Text($"Based on retained History · kept for {KeptFor}. Older entries may lack duration or app data.", "MurmurCaption"));
        return root;
    }

    /// <summary>LazyVGrid(.adaptive(minimum:)): as many equal columns as fit, at least one.</summary>
    static UIElement Adaptive(List<UIElement> items, double min, double colGap, double rowGap)
    {
        var grid = new Grid { ColumnSpacing = colGap, RowSpacing = rowGap };
        int columns = 0;
        void Layout(double width)
        {
            int next = Math.Max(1, (int)Math.Floor((width + colGap) / (min + colGap)));
            if (next == columns) return;
            columns = next;
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear(); grid.Children.Clear();
            for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < items.Count; i++)
            {
                if (i % columns == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow((FrameworkElement)items[i], i / columns); Grid.SetColumn((FrameworkElement)items[i], i % columns);
                grid.Children.Add(items[i]);
            }
        }
        grid.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        return grid;
    }

    // ---- charts ----

    /// <summary>Bars in the accent colour with a trailing value axis (hairline gridlines, like Swift Charts) and sparse labels under the plot.</summary>
    static UIElement BarChart(string name, IReadOnlyList<int> values, Func<int, string> xLabel, IReadOnlyList<int> labelAt, Func<int, string> describe, double height = 264)
    {
        int top = NiceMax(values.Count == 0 ? 0 : values.Max());
        var plot = new Canvas { Height = height - 22 };
        var labels = new Canvas { Height = 18 };
        var bars = new List<Rectangle>();
        for (int i = 0; i < values.Count; i++)
        {
            var bar = new Rectangle { Style = null, Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MurmurAccent"], RadiusX = 1, RadiusY = 1 };
            AutomationProperties.SetName(bar, describe(i));
            ToolTipService.SetToolTip(bar, describe(i));
            bars.Add(bar); plot.Children.Add(bar);
        }
        var lines = new List<(Border Line, TextBlock Label, double Fraction)>();
        foreach (double f in new[] { 0.0, 0.5, 1.0 })
        {
            var line = MurmurStyle.HRule(true); var label = MurmurStyle.Text(((int)Math.Round(top * f)).ToString("N0"), "MurmurCaption"); label.FontSize = 11; label.TextWrapping = TextWrapping.NoWrap;
            plot.Children.Insert(0, line); plot.Children.Add(label); lines.Add((line, label, f));
        }
        var xs = labelAt.Select(i => (Index: i, Text: MurmurStyle.Text(xLabel(i), "MurmurCaption"))).ToList();
        foreach (var x in xs) { x.Text.FontSize = 11; x.Text.TextWrapping = TextWrapping.NoWrap; labels.Children.Add(x.Text); }
        const double axis = 34;
        void Layout()
        {
            double w = plot.ActualWidth - axis, h = plot.Height;
            if (w <= 0) return;
            double slot = w / Math.Max(1, values.Count), bw = Math.Max(1, slot * 0.7);
            for (int i = 0; i < values.Count; i++)
            {
                double bh = top == 0 ? 0 : h * values[i] / top;
                bars[i].Width = bw; bars[i].Height = Math.Max(values[i] > 0 ? 1 : 0, bh);
                Canvas.SetLeft(bars[i], i * slot + (slot - bw) / 2); Canvas.SetTop(bars[i], h - bars[i].Height);
            }
            foreach (var (line, label, f) in lines)
            {
                double y = h - h * f;
                line.Width = w; Canvas.SetTop(line, Math.Min(h - 1, y)); Canvas.SetLeft(line, 0);
                Canvas.SetLeft(label, w + 6); Canvas.SetTop(label, Math.Max(0, y - 8));
            }
            foreach (var (index, text) in xs)
            {
                text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double center = index * slot + slot / 2;
                Canvas.SetLeft(text, Math.Clamp(center - text.DesiredSize.Width / 2, 0, Math.Max(0, w - text.DesiredSize.Width)));
            }
        }
        plot.SizeChanged += (_, _) => Layout();
        var chart = new StackPanel { Children = { plot, labels } };
        AutomationProperties.SetName(chart, name);
        return chart;
    }

    static int NiceMax(int max)
    {
        if (max <= 0) return 4;
        if (max <= 4) return max % 2 == 0 ? max : max + 1;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (double m in new[] { 1, 2, 2.5, 5, 10 }) if (m * pow >= max) return (int)(m * pow);
        return (int)(10 * pow);
    }

    static StackPanel Block(UIElement head, UIElement body, double height = 300) => new() { Spacing = 12, MinHeight = height, Children = { head, body } };

    static UIElement Daily(Insights.Stats st)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).ToList();
        var values = days.Select(d => st.DailyWords.GetValueOrDefault(d)).ToList();
        return Block(Heading("Daily activity", "Words per day over the last 30 days, in this PC’s time zone. Counts include only retained History."),
            BarChart("Daily activity", values, i => days[i].ToString("MMM d", Culture), (int[])[0, 10, 20], i => $"{days[i].ToString("MMM d", Culture)}: {values[i]} words"));
    }

    static UIElement Hourly(Insights.Stats st)
    {
        string Hour(int h) => h == 0 ? "12am" : h < 12 ? $"{h}am" : h == 12 ? "12pm" : $"{h - 12}pm";
        var values = st.HourlyDictations.ToList();
        return Block(Heading("Busiest hours", "Dictations started in each hour, using this PC’s current time zone. Includes all retained History; not recording duration."),
            BarChart("Busiest hours", values, Hour, (int[])[0, 6, 12, 18], i => $"{i}:00–{i}:59: {values[i]} dictations"));
    }

    UIElement StreakCard(Insights.Stats st)
    {
        UIElement Stat(int days, string label)
        {
            var v = MurmurStyle.Text($"{days} {(days == 1 ? "day" : "days")}", "MurmurSettingLabel"); v.FontSize = 22; v.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            var l = MurmurStyle.Text(label.ToUpperInvariant(), "MurmurSection"); l.FontSize = 11;
            return new StackPanel { Children = { v, l } };
        }
        var totals = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { Stat(st.CurrentStreak, "Current"), Stat(st.LongestStreak, "Longest retained") } };
        var cutoff = s.Settings.Get(Prefs.Retention).Cutoff(DateTime.UtcNow, TimeZoneInfo.Local);
        return Block(Heading("Streak", "Calendar shows the last five weeks. A day counts after one dictation in this PC’s time zone. Streak totals use all retained History."),
            new StackPanel { Spacing = 12, Children = { totals, Calendar(st.Days, 5, cutoff) } });
    }

    static UIElement Calendar(IReadOnlySet<DateOnly> days, int weeks, DateTime? cutoff)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var first = monday.AddDays(-7 * (weeks - 1));
        var firstKept = cutoff is { } cut ? DateOnly.FromDateTime(cut.ToLocalTime()) : (DateOnly?)null;
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r <= weeks; r++) grid.RowDefinitions.Add(new RowDefinition { Height = r == 0 ? GridLength.Auto : new GridLength(26) });
        for (int c = 0; c < 7; c++)
        {
            var name = Culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((c + 1) % 7));
            var t = MurmurStyle.Text(name, "MurmurCaption"); t.FontSize = 11; t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 0, 0, 4);
            Grid.SetColumn(t, c); grid.Children.Add(t);
        }
        for (int i = 0; i < weeks * 7; i++)
        {
            var day = first.AddDays(i);
            bool on = days.Contains(day);
            var label = new TextBlock { Text = day.Day.ToString(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Style = MurmurStyle.Style(on ? "MurmurPillWhite" : "MurmurTabOff") };
            label.FontSize = 11; label.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            var pill = new Border { Child = label, CornerRadius = new CornerRadius(11.5), Style = MurmurStyle.Style(on ? "MurmurBar" : "MurmurNavOn") };
            var cell = new Border { Child = pill, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1.5) };
            if (day == today) cell.Style = MurmurStyle.Style("MurmurPillOutline");
            cell.BorderThickness = new Thickness(1.5);
            cell.Opacity = day > today || (firstKept is { } fk && day < fk) ? 0.3 : 1;
            ToolTipService.SetToolTip(cell, day.ToString("MMM d, yyyy", Culture) + (on ? ": dictated" : ": no retained dictation"));
            Grid.SetRow(cell, 1 + i / 7); Grid.SetColumn(cell, i % 7); grid.Children.Add(cell);
        }
        AutomationProperties.SetName(grid, $"Dictation days: {days.Count(d => d >= first && d <= today)} days with dictation in the last {weeks} weeks");
        return grid;
    }

    UIElement TopApps(Insights.Stats st)
    {
        var list = new StackPanel { Spacing = 12 };
        if (st.Apps.Count == 0) list.Children.Add(MurmurStyle.Text("App usage appears after a destination app is recorded."));
        int max = Math.Max(st.Apps.FirstOrDefault()?.Words ?? 1, 1);
        foreach (var item in st.Apps.Take(6))
        {
            var name = MurmurStyle.Text(AppName(item.App), "MurmurSettingLabel"); name.FontSize = 14; name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
            var words = MurmurStyle.Text($"{item.Words:N0} words", "MurmurCaption"); words.TextWrapping = TextWrapping.NoWrap;
            var head = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { name, words } };
            Grid.SetColumn(words, 1);
            var fill = new Border { Height = 5, HorizontalAlignment = HorizontalAlignment.Left, Style = MurmurStyle.Style("MurmurBar") };
            var track = new Grid { Height = 5, Children = { new Border { Style = MurmurStyle.Style("MurmurTrack") }, fill } };
            double share = (double)item.Words / max;
            track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * share;
            var row = new StackPanel { Spacing = 5, Children = { head, track } };
            AutomationProperties.SetName(row, $"{AppName(item.App)}, {item.Words} words, {item.Count} dictations");
            list.Children.Add(row);
        }
        return Block(Heading("Top apps", $"Ranked by words in retained History. {st.WithoutApp} dictations have no destination app recorded."), list);
    }

    /// <summary>History stores a lowercased process name on Windows ("chrome"); show it capitalised without ".exe".</summary>
    internal static string AppName(string app)
    {
        var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        return name.Length == 0 ? app : char.ToUpper(name[0], Culture) + name[1..];
    }
}
