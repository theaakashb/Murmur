namespace Murmur.App;

/// <summary>
/// Development aid: <c>Murmur.exe --bubble-demo &lt;state&gt; &lt;seconds&gt; [light|dark]</c> shows the real pill window in a state for N seconds (a synthetic
/// microphone level, words typed in over time) and exits. States: listening, processing, label, live-words, live-start, notice, error, card, changes, all
/// (each in turn for N seconds). Never reads settings or user data, and Copy does not touch the clipboard.
/// </summary>
internal sealed partial class Bubble
{
    static readonly string[] DemoStates = ["listening", "processing", "label", "live-start", "live-words", "notice", "error", "card", "changes"];
    const string DemoSentence = "Add two eggs and a litre of milk to the shopping list";

    internal async Task DemoAsync(string state, double seconds)
    {
        foreach (var name in state == "all" ? DemoStates : [state])
        {
            await RunDemoState(name, seconds);
            Hide();
            await Task.Delay(500);
        }
    }

    async Task RunDemoState(string name, double seconds)
    {
        switch (name)
        {
            case "listening": ShowListening(); break;
            case "processing": ShowProcessing(); break;
            case "label": ShowProcessing("Editing with your Custom Provider…"); break;
            case "live-start": ShowListening(); ShowLiveWords(); break;
            case "live-words": ShowListening(); ShowLiveWords(); break;
            case "notice": ShowNotice("Dictation saved", seconds + 5); break;
            case "error": ShowError("The microphone is not available.", seconds + 5); break;
            case "card": ShowCard("Could not paste the dictation. Copy it here, or select a text field and press Ctrl+Win+Shift+V.", "Add two eggs and a litre of milk to the shopping list. Then call the plumber about the kitchen tap."); break;
            case "changes": ShowChanges(3, true, "original words"); break;
            default: throw new ArgumentException($"Unknown state '{name}'. States: {string.Join(", ", DemoStates)}, all.");
        }
        var start = DateTime.UtcNow;
        var words = DemoSentence.Split(' ');
        while ((DateTime.UtcNow - start).TotalSeconds < seconds)
        {
            double t = (DateTime.UtcNow - start).TotalSeconds;
            // A speech-like level: syllable pulses with pauses.
            SetLevel((float)Math.Max(0, 0.55 * Math.Sin(t * 5.3) * Math.Sin(t * 1.3) + 0.25 * Math.Sin(t * 11)));
            if (name == "live-words") SetPartial(string.Join(' ', words.Take(Math.Min(words.Length, 1 + (int)(t / 0.45)))));
            await Task.Delay(30);
        }
    }
}
