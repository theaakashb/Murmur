using Murmur.Core;

namespace Murmur.App;

/// <summary>Port of app/TipsUI.swift's TipPresenter: queues milestones and the voice-edit tip for the floating <see cref="TipPanel"/>, and works out the
/// "new" dots and pane intros on Settings. The rules live in <see cref="Tips"/>; this owns the queue and the timing. UI thread only.</summary>
internal sealed class TipPresenter
{
    public enum Kind { Milestone, VoiceEdit }
    public sealed record Card(Kind Kind, Tips.Milestone? Milestone = null)
    {
        public Tips.ID Id => Kind == Kind.Milestone ? Tips.ID.Milestone : Tips.ID.VoiceEdit;
        /// <summary>Milestones are news and leave on their own; the voice-edit tip waits like the call offer.</summary>
        public double Lifetime => Kind == Kind.Milestone ? 8 : CallDetector.OfferLifetime;
    }

    readonly AppServices s;
    readonly Controller controller;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    Card? pending;
    long shownAt;

    public TipPresenter(AppServices services, Controller controller)
    {
        s = services; this.controller = controller;
        timer = services.UI.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    Tips Ledger => new(s.Settings);

    /// <summary>The card on screen, or null.</summary>
    public Card? Current { get; private set; }
    public event Action<Card>? Shown;
    public event Action? Hidden;
    /// <summary>The ledger changed: Settings redraws its dots and intros.</summary>
    public event Action? Changed;

    IReadOnlySet<Tips.Pane> dots = new HashSet<Tips.Pane>();
    IReadOnlyList<Tips.Pane> intros = [];
    public bool AnyDot => dots.Count > 0;
    public bool HasDot(string tab) => dots.Any(p => p.ToString() == tab);
    public bool HasIntro(string tab) => intros.Any(p => p.ToString() == tab);

    public bool Enabled
    {
        get => Ledger.Enabled;
        set { Ledger.SetEnabled(value); if (!value) { pending = null; Hide(); } Refresh(); }
    }

    /// <summary>Recomputes the dots and intros from use. Called when the ledger or the use it reads changes.</summary>
    public void Refresh()
    {
        var tips = Ledger;
        var usage = CurrentUsage(tips);
        intros = tips.Eligible(usage); dots = tips.Dots(usage);
        Changed?.Invoke();
    }

    Insights.Stats? Stats()
    {
        try { return Insights.Compute(s.History.Entries().Select(InsightsItem.FromHistory), true, DateTimeOffset.Now, TimeZoneInfo.Local) as Insights.Stats; }
        catch (Exception) { return null; }
    }

    Tips.Usage CurrentUsage(Tips tips)
    {
        var stats = Stats();
        int learned = s.Dictionary?.Entries.Count(e => e.Source == EntrySource.Learned) ?? 0;
        return new(stats?.Dictations ?? 0, stats?.Apps.Count ?? 0, learned, tips.DaysUsed(DateTimeOffset.Now));
    }

    public Tips.Usage Usage => CurrentUsage(Ledger);

    /// <summary>Delete All Data: nothing queued from before survives, and the switch shows the default again.</summary>
    public void Reset() { pending = null; Hide(); Refresh(); }

    /// <summary>One inserted dictation: counts its words and queues a milestone when it crosses one.</summary>
    public void RecordDictation(string final, double? seconds)
    {
        (int, double, DateTimeOffset?) Seed()
        {
            // A login launch may not have read History yet; seeding 0 would undercount forever.
            var stats = Stats();
            return stats is null ? (0, 0, null) : (stats.Words, stats.SecondsSaved ?? 0, stats.Days.Count == 0 ? null : new DateTimeOffset(stats.Days.Min().ToDateTime(TimeOnly.MinValue)));
        }
        var milestone = Ledger.Record(Insights.SplitWords(final).Count, Tips.SecondsSaved(final, seconds), DateTimeOffset.Now, Seed);
        Refresh();
        if (milestone is not null) Queue(new Card(Kind.Milestone, milestone));
    }

    /// <summary>The correction watch ended: a hand rewrite, twice in a week, by someone who has not used voice edit.</summary>
    public void NoteAfterPaste(string initial, string edited)
    {
        var tips = Ledger;
        if (tips.IsDone(Tips.ID.VoiceEdit) || !controller.VoiceEditReady || s.History.Entries().Any(e => e.EditOriginal is not null)) return;
        if (Tips.IsRewrite(initial, edited) && tips.NoteRewrite(DateTimeOffset.Now)) Queue(new Card(Kind.VoiceEdit));
    }

    public void VoiceEditUsed()
    {
        Ledger.Completed(Tips.ID.VoiceEdit);
        if (Current?.Kind == Kind.VoiceEdit || pending?.Kind == Kind.VoiceEdit) { pending = null; Hide(); }
    }

    void Queue(Card next) { if (Ledger.CanShow(next.Id, DateTimeOffset.Now)) pending = next; }

    /// <summary>A queued tip waits for a pause, never over dictation, a meeting or the call offer.</summary>
    void Tick()
    {
        bool busy = controller.IsBusy || controller.MeetingLive || controller.CallOfferShowing;
        double age = System.Diagnostics.Stopwatch.GetElapsedTime(shownAt).TotalSeconds;
        // The tip shares the indicator's spot: it gives way to anything else there, without counting as dismissed.
        if (Current is { } card && (busy || age >= card.Lifetime)) Hide();
        if (pending is not { } next || Current is not null || busy) return;
        if (!Ledger.CanShow(next.Id, DateTimeOffset.Now) || (next.Kind == Kind.VoiceEdit && !controller.VoiceEditReady)) { pending = null; return; }
        Show(next);
    }

    void Show(Card next)
    {
        pending = null; Current = next; shownAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Ledger.Shown(DateTimeOffset.Now);
        Shown?.Invoke(next);
    }

    public void Hide() { Current = null; Hidden?.Invoke(); }

    /// <summary>Milestones are news, not a nudge: closing one never stops the next. Only the voice-edit tip retires.</summary>
    public void Dismiss() { if (Current?.Kind == Kind.VoiceEdit) Ledger.Dismissed(Tips.ID.VoiceEdit); Hide(); }
    public void Never() { if (Current?.Kind == Kind.VoiceEdit) Ledger.Completed(Tips.ID.VoiceEdit); Hide(); }

    public void Act(Action<string, string> openSettings, Action openHome)
    {
        if (Current is not { } card) return;
        if (card.Kind == Kind.VoiceEdit) Ledger.Completed(Tips.ID.VoiceEdit);
        Hide();
        if (card.Kind == Kind.VoiceEdit) openSettings("Dictation", "Recording"); else openHome();
    }

    public string VoiceEditHow()
    {
        var edit = s.Settings.Get(Prefs.VoiceEditShortcut);
        var key = ShortcutSpec.Label(edit == EditTrigger.SameKey ? s.Settings.Get(Prefs.Shortcut) : edit);
        return $"Select the text, press {key} and say what to change, like “make it shorter”.";
    }

    public string Spoken(Card card) => card.Kind == Kind.Milestone
        ? $"Milestone: {card.Milestone!.Words:N0} words. About {Tips.Duration(card.Milestone.SecondsSaved)} of typing saved."
        : "Tip: " + VoiceEditHow();

    // ---- explore dots and pane intros ----

    /// <summary>A settings tab was opened: its dot goes.</summary>
    public void Opened(string tab)
    {
        if (!HasDot(tab)) return;
        Ledger.Opened(Enum.Parse<Tips.Pane>(tab)); Refresh();
    }

    public void FinishIntro(string tab)
    {
        if (!Enum.TryParse<Tips.Pane>(tab, out var pane)) return;
        var tips = Ledger;
        if (Tips.IdFor(pane) is { } id) tips.Completed(id);
        tips.Opened(pane); Refresh();
    }

    public void ShowAgain() { Ledger.Reset(); Refresh(); }

    public static (string Title, string Body)? IntroText(string tab, Tips.Usage usage) => tab switch
    {
        "Modes" => ("Different apps, different voice.", $"You dictate in {usage.Apps} apps. A mode can keep Mail formal and chat casual, and Murmur picks it by app."),
        "Dictionary" => ("Murmur is learning your words.", $"It learned {(usage.LearnedWords == 1 ? "1 word" : $"{usage.LearnedWords} words")} from your corrections. Check the spellings below."),
        "Cues" => ("Stop retyping what you repeat.", "Save a link, an address or a sign-off you repeat, then say its cue and Murmur inserts it exactly."),
        _ => null,
    };
}
