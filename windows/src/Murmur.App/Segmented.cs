using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Murmur.App;

/// <summary>The Mac's underline-tab segmented control: muted labels, the selected one in ink with a 2 px accent underline over a hairline.
/// <paramref name="stretch"/> divides the width equally (Cleanup style, routes); otherwise tabs size to their text.</summary>
internal sealed class Segmented : Grid
{
    readonly List<Button> buttons = [];
    readonly List<Border> bars = [];
    readonly List<TextBlock> labels = [];
    readonly Dictionary<int, FontIcon> clouds = [];
    readonly List<Microsoft.UI.Xaml.Shapes.Ellipse> dots = [];
    int selected = -1;
    readonly double fontSize;

    public Segmented(string name, string[] items, int selected, Action<int> changed, bool stretch = true, int[]? cloud = null, double fontSize = 14)
    {
        AutomationProperties.SetName(this, name);
        this.fontSize = fontSize;
        var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Style = MurmurStyle.Style("MurmurRuleLine"), IsHitTestVisible = false };
        SetColumnSpan(rule, 99);
        Children.Add(rule);
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var text = new TextBlock { Text = items[i], FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { text } };
            if (cloud?.Contains(i) == true)
            {
                var icon = new FontIcon { Glyph = "", FontSize = 13, Foreground = (Brush)Application.Current.Resources["MurmurAccent"] };
                clouds[i] = icon;
                content.Children.Add(icon);
            }
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = (Brush)Application.Current.Resources["MurmurAccent"], VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAccessibilityView(dot, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            content.Children.Add(dot); dots.Add(dot);
            var bar = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Style = MurmurStyle.Style("MurmurBubbleDot"), Opacity = 0 };
            var button = new Button
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                Padding = new Thickness(0), HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                Content = new Grid { Children = { new Border { Child = content, Padding = new Thickness(4, 12, 4, 14) }, bar } }
            };
            AutomationProperties.SetName(button, items[i]);
            button.Click += (_, _) => { if (this.selected != index) { Select(index); changed(index); } };
            ColumnDefinitions.Add(new ColumnDefinition { Width = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            if (!stretch && i > 0) button.Margin = new Thickness(32, 0, 0, 0);
            SetColumn(button, i);
            Children.Add(button);
            buttons.Add(button); bars.Add(bar); labels.Add(text);
        }
        Select(selected);
    }

    public int SelectedIndex => selected;

    public void Select(int index)
    {
        selected = index;
        for (int i = 0; i < buttons.Count; i++)
        {
            bool on = i == index;
            labels[i].Style = MurmurStyle.Style(on ? "MurmurSettingLabel" : "MurmurTabOff");
            labels[i].FontSize = fontSize;
            labels[i].FontWeight = on ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.Normal;
            bars[i].Opacity = on ? 1 : 0;
            AutomationProperties.SetAccessibilityView(bars[i], Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }
    }

    /// <summary>The Mac's ExploreDot beside a tab with something new in it.</summary>
    public void SetDot(int index, bool shown) => dots[index].Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

    public void SetCloud(int index, bool shown) => clouds[index].Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

    public void SetEnabled(int index, bool enabled) => buttons[index].IsEnabled = enabled;
}
