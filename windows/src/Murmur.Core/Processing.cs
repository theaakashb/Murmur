// Port of app/Processing.swift.
// Text processing between transcription and insertion: dictionary, then optional cleanup, then the bound check.
//
// Order: DictionaryStore.Apply runs first and in every mode (faithful included), so its preferred spellings are already in the
// text the cleanup guard compares against. Relevant entries are also passed as allowed terms, and as prompt terms on the cloud route.
// Cues (VoiceCues.cs) are matched on the raw transcript before any of this: every stretch of spoken words takes the path below on
// its own, and cue text bypasses it.
//
// Failure policy: a dictation is never held. When the guard rejects the cleaned words, the transcript's own words go in with the
// model's punctuation (CleanupPipeline.Punctuated), retaining guarded rule-based spoken corrections. A provider error, or a transfer
// that also fails the guard, falls back to local rules, and failing those, to the dictionary-applied transcript. Reasons stay content-free.
//
// Windows only: a RegexMatchTimeoutException (Swift's NSRegularExpression has no timeout) anywhere in prompt building, the guard,
// the punctuation transfer, local rules or tone takes that same rejection path, recorded as CleanerFailed.
using System.Text;
using System.Text.RegularExpressions;

namespace Murmur.Core;

/// <summary>Where cleanup runs. <c>Local</c> is the RuleCleaner. <c>Cloud</c> means "through an <see cref="ICleanupProvider"/>":
/// a custom OpenAI-compatible endpoint, Murmur Cloud or the managed local model (see <see cref="CleanupChoice"/>). Raw values "local", "cloud".</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(CamelCaseEnumConverter<CleanupRoute>))]
public enum CleanupRoute { Local, Cloud }

/// <summary>What the controller does with a finished transcript.</summary>
public abstract record ProcessingOutcome
{
    ProcessingOutcome() { }

    /// <summary>Insert (or save, if insertion is off or unsafe) this text.</summary>
    public sealed record Insert(string Text) : ProcessingOutcome;
    /// <summary>Save <paramref name="Raw"/> for recovery and do not insert automatically. Only the controller produces this
    /// (processing unavailable); the reason is content-free.</summary>
    public sealed record HeldForRecovery(string Raw, CleanupFailureReason Reason) : ProcessingOutcome;
}

/// <summary>One dictation's outcome and its content-free (or History-only) evidence.</summary>
public sealed class ProcessingResult(ProcessingOutcome outcome)
{
    public ProcessingOutcome Outcome { get; set; } = outcome;
    public ProviderError? ProviderError { get; set; }
    public CleanupFailureReason? Fallback { get; set; }
    public string? RejectedOutput { get; set; }
    public CleanupPath? CleanupPath { get; set; }
    public string? ModelOutput { get; set; }
    public string? GuardVerdict { get; set; }
    public string? PromptHash { get; set; }
    public List<string> Terms { get; set; } = [];
    public string? CleanupInput { get; set; }
    /// <summary>Screen context went to the cleanup provider with this dictation.</summary>
    public bool ContextUsed { get; set; }
    /// <summary>The engine provider that ran a writing-mode dictation, for History; null otherwise.</summary>
    public ProviderID? Engine { get; set; }
    /// <summary>How many cues were expanded; never their phrases or text.</summary>
    public int Cues { get; set; }

    /// <summary>The text to insert, or null when the outcome holds it.</summary>
    public string? Text => Outcome is ProcessingOutcome.Insert i ? i.Text : null;
}

/// <summary>Settings owned by processing, persisted in the settings store (keys and defaults: <see cref="Prefs"/>).</summary>
public sealed record ProcessingSettings
{
    public CleanupOptions Options { get; init; } = Make(s => s.Fallback);
    /// <summary>Derived from <see cref="RouteSettings.Cleanup"/> by the app controller; persisted here.</summary>
    public CleanupRoute Route { get; init; } = RouteFrom(Prefs.CleanupRoute.Fallback);
    public bool LearningEnabled { get; init; } = Prefs.LearningEnabled.Fallback;
    public bool ScreenContext { get; init; } = Prefs.ScreenContext.Fallback;

    static CleanupRoute RouteFrom(string raw) => raw == CleanupRoute.Cloud.RawValue() ? CleanupRoute.Cloud : CleanupRoute.Local;

    static CleanupOptions Make(Func<Setting<bool>, bool> flag) => OptionsFor(flag(Prefs.CleanupFillers), flag(Prefs.CleanupCorrections), flag(Prefs.CleanupLists),
        flag(Prefs.CleanupPunctuation), flag(Prefs.CleanupPolish), flag(Prefs.CleanupProfessional), flag(Prefs.CleanupSpoken));

    /// <summary>The options for these cleanup flags; every settings source builds them here, with <c>TrustModel</c> on.</summary>
    public static CleanupOptions OptionsFor(bool fillers, bool corrections, bool lists, bool punctuation, bool polish, bool professional, bool spoken) =>
        new(fillers, corrections, lists, punctuation, polish, professional, spoken, trustModel: true);

    /// <summary>Missing or unknown values fall back to the approved defaults (all comforts on, local route).</summary>
    public static ProcessingSettings Load(ISettingsStore defaults) => new()
    {
        Options = Make(defaults.Get),
        Route = RouteFrom(defaults.Get(Prefs.CleanupRoute)),
        LearningEnabled = defaults.Get(Prefs.LearningEnabled),
        ScreenContext = defaults.Get(Prefs.ScreenContext),
    };

    public void Save(ISettingsStore defaults)
    {
        defaults.Set(Prefs.CleanupFillers, Options.Fillers);
        defaults.Set(Prefs.CleanupCorrections, Options.Corrections);
        defaults.Set(Prefs.CleanupLists, Options.Lists);
        defaults.Set(Prefs.CleanupPunctuation, Options.Punctuation);
        defaults.Set(Prefs.CleanupPolish, Options.Polish);
        defaults.Set(Prefs.CleanupProfessional, Options.Professional);
        defaults.Set(Prefs.CleanupSpoken, Options.Spoken);
        defaults.Set(Prefs.CleanupRoute, Route.RawValue());
        defaults.Set(Prefs.LearningEnabled, LearningEnabled);
        defaults.Set(Prefs.ScreenContext, ScreenContext);
    }

    /// <summary>Swift <c>DictationStyle(mode:settings:context:)</c>.</summary>
    public DictationStyle Style(WritingMode? mode = null, ScreenContext? context = null) => DictationStyle.ForMode(mode, Options, ScreenContext, context);
}

/// <summary>Owns the dictionary, correction ledger and cues and turns a raw transcript into an outcome.
/// Not thread-safe: call it from one context (the UI thread). <see cref="ProcessAsync"/> resumes on the caller's context; only the
/// provider calls and their guards run on the thread pool.</summary>
public sealed class ProcessingCoordinator
{
    public DictionaryStore Dictionary { get; }
    public CorrectionLedger Ledger { get; }
    public VoiceCueStore Cues { get; }
    public ProcessingSettings Settings { get; private set; }
    readonly ISettingsStore defaults;
    /// <summary>The local route's cleaner. Tests substitute failing or misbehaving cleaners.</summary>
    public ICleaner LocalCleaner { get; set; } = new RuleCleaner();
    /// <summary>The cloud route's provider. Null means the cloud route is not configured, so local rules run.
    /// The app controller sets it from <see cref="RoutePlan"/> (selected provider and stored key) at each start.</summary>
    public ICleanupProvider? CloudCleaner { get; set; }
    /// <summary>The AI engine (<see cref="AIEngine"/>) that writing-mode dictations use; null when none is available.</summary>
    public ICleanupProvider? EngineCleaner { get; set; }
    /// <summary>Test seam: called with a stage name ("prompt", "judge", "punctuated", "local", "transform", "tone") just before that
    /// stage's regex work, so a test can throw <see cref="RegexMatchTimeoutException"/> exactly there.</summary>
    internal Action<string>? Probe { get; set; }
    /// <summary>What the punctuation transfer is guarded as: sentence punctuation and capitals, no word changes.</summary>
    public static readonly CleanupOptions PunctuationOnly = new(fillers: false, corrections: false, lists: false, punctuation: true);

    /// <summary><paramref name="directory"/> holds dictionary.json, ledger.json and cues.json. <paramref name="isCommonWord"/> guards learning and
    /// dictionary respelling (the bundled English word list in the app); it may be called from any thread.</summary>
    public ProcessingCoordinator(string directory, ISettingsStore defaults, Func<string, bool>? isCommonWord = null)
    {
        this.defaults = defaults;
        Dictionary = new DictionaryStore(directory, isCommonWord);
        Ledger = new CorrectionLedger(directory, Dictionary);
        Cues = new VoiceCueStore(directory);
        Settings = ProcessingSettings.Load(defaults);
        if (Ledger.LearningEnabled != Settings.LearningEnabled) Ledger.SetLearningEnabled(Settings.LearningEnabled);
    }

    /// <summary>Persists settings and keeps the ledger's learning switch in step with them.</summary>
    public void Update(ProcessingSettings next, bool persist = true)
    {
        if (Ledger.LearningEnabled != next.LearningEnabled) Ledger.SetLearningEnabled(next.LearningEnabled);
        Settings = next;
        if (persist) next.Save(defaults);
    }

    (string Text, List<string> Terms) Prepare(string raw)
    {
        var applied = Dictionary.Apply(raw);
        var seen = new HashSet<Guid>();
        var terms = new List<string>();
        foreach (var entry in Dictionary.RelevantEntries(raw).Concat(Dictionary.RelevantEntries(applied)))
            if (seen.Add(entry.Id)) terms.Add(entry.Preferred);
        return (applied, terms);
    }

    /// <summary>Local rules, guarded; the dictionary-applied text itself if they fail the guard.</summary>
    string LocalClean(string text, IReadOnlyList<string> terms, CleanupOptions options, ProcessingResult evidence)
    {
        var local = LocalRules(text, terms, options, LocalCleaner, Probe);
        if (local.Reason is { } reason) evidence.Fallback ??= reason;
        return local.Text;
    }

    /// <summary>Every enabled local operation (spoken commands, fillers, corrections, lists), guarded. A null reason means the rules
    /// passed the guard; otherwise the text is the input with only the spoken commands and "instead" corrections resolved
    /// (or the input itself, after a regex timeout).</summary>
    public static (string Text, CleanupFailureReason? Reason) LocalRules(string text, IReadOnlyList<string> terms, CleanupOptions options, ICleaner? cleaner = null) =>
        LocalRules(text, terms, options, cleaner ?? new RuleCleaner(), null);

    static (string Text, CleanupFailureReason? Reason) LocalRules(string input, IReadOnlyList<string> terms, CleanupOptions options, ICleaner cleaner, Action<string>? probe)
    {
        try
        {
            probe?.Invoke("local");
            var text = options.Corrections && options.Polish ? RuleCleaner.ApplyInsteadCorrections(options.Spoken ? RuleCleaner.SpokenCommands(input) : input) : input;
            // Broader AI grammar and phrasing edits have no local fallback; trust is for the AI model, never local rules.
            var strict = options with { Polish = false, Professional = false, TrustModel = false };
            return CleanupPipeline.Process(text, strict, cleaner, terms) switch
            {
                CleanupOutcome.Cleaned c => (c.Text, null),
                CleanupOutcome.Unchanged u => (u.Text, null),
                CleanupOutcome.HeldForRecovery h => (text, h.Reason),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (RegexMatchTimeoutException) { return (input, CleanupFailureReason.CleanerFailed); }
    }

    /// <summary>Synchronous path, used on the local route. It never calls the network: on the cloud route it uses local rules.
    /// The app calls <see cref="ProcessAsync"/> whenever the route is not local rules. <paramref name="style"/> carries a writing mode's
    /// overrides; null uses the settings. Screen context and instructions are never used here.</summary>
    public ProcessingResult Process(string raw, DictationStyle? style = null)
    {
        try
        {
            if (VoiceCueText.Split(raw, Cues.Cues) is not { } pieces) return ProcessSegment(raw, style);
            return Assemble(pieces, pieces.Select(p => p is VoiceCuePiece.Text t ? ProcessSegment(t.Raw, style) : null).ToList());
        }
        catch (RegexMatchTimeoutException) { return LastResort(raw); }
    }

    /// <summary>Outer net for a timeout outside the guarded stages: the dictionary-applied transcript goes in.</summary>
    ProcessingResult LastResort(string raw) => new(new ProcessingOutcome.Insert(Dictionary.Apply(raw))) { Fallback = CleanupFailureReason.CleanerFailed, CleanupPath = Core.CleanupPath.LocalRules };

    ProcessingResult ProcessSegment(string raw, DictationStyle? style)
    {
        var evidence = new ProcessingResult(new ProcessingOutcome.Insert(raw));
        style ??= Settings.Style();
        var prepared = Prepare(raw);
        evidence.Terms = prepared.Terms; evidence.CleanupInput = prepared.Text == raw ? null : prepared.Text;
        // Reaching here on the cloud route is a failure only for styles that wanted the model.
        var wantedModel = Settings.Route == CleanupRoute.Cloud && style.UsesModel;
        if (wantedModel && style.Options.AnyEnabled) evidence.Fallback = CleanupFailureReason.CleanerFailed;
        evidence.CleanupPath = !style.Options.AnyEnabled ? Core.CleanupPath.Off : wantedModel ? Core.CleanupPath.LocalRules : Core.CleanupPath.LocalRoute;
        evidence.Outcome = new ProcessingOutcome.Insert(Toned(LocalClean(prepared.Text, prepared.Terms, style.Options, evidence), prepared.Text, style));
        return evidence;
    }

    /// <summary>The mode's tone, applied last to whichever text was chosen. Never on the email layout, and only as exactly <c>Tone.Apply</c>.</summary>
    string Toned(string text, string input, DictationStyle style)
    {
        if (!style.Options.AnyEnabled || style.Tone == Tone.Formal || text.Length == 0) return text;
        try
        {
            Probe?.Invoke("tone");
            return style.Options.Spoken && RuleCleaner.EmailLayout(input) is not null ? text : style.Tone.Apply(text);
        }
        catch (RegexMatchTimeoutException) { return text; }
    }

    /// <summary>Full path including the cloud route. Always inserts: see the failure policy above. Call it from the context that owns this
    /// coordinator; it resumes there. <paramref name="cancellationToken"/> reaches the provider; a cancelled call falls back like any provider error.</summary>
    public async Task<ProcessingResult> ProcessAsync(string raw, DictationStyle? style = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (VoiceCueText.Split(raw, Cues.Cues) is not { } pieces) return await ProcessSegmentAsync(raw, style, cancellationToken);
            var results = new List<ProcessingResult?>();
            foreach (var piece in pieces)
                results.Add(piece is VoiceCuePiece.Text t ? await ProcessSegmentAsync(t.Raw, style, cancellationToken) : null);
            return Assemble(pieces, results);
        }
        catch (RegexMatchTimeoutException) { return LastResort(raw); }
    }

    /// <summary>A dictation with cue commands: each stretch of spoken words went through the ordinary path on its own, the expansions are
    /// never sent anywhere, and the pieces are joined. A whole-dictation cue has no stretch, so no cleaner or provider runs.</summary>
    ProcessingResult Assemble(IReadOnlyList<VoiceCuePiece> pieces, IReadOnlyList<ProcessingResult?> results)
    {
        var evidence = new ProcessingResult(new ProcessingOutcome.Insert("")) { Cues = pieces.Count(p => p is VoiceCuePiece.Expansion) };
        var done = results.OfType<ProcessingResult>().ToList();
        static string? Joined(IEnumerable<string?> parts, string separator) { var list = parts.OfType<string>().ToList(); return list.Count == 0 ? null : string.Join(separator, list); }
        CleanupPath?[] order = [Core.CleanupPath.LocalRules, Core.CleanupPath.ModelPunctuationOnLocal, Core.CleanupPath.ModelAccepted, Core.CleanupPath.LocalRoute, Core.CleanupPath.Off];
        evidence.CleanupPath = done.Count == 0 ? Core.CleanupPath.Off : order.FirstOrDefault(path => done.Any(d => d.CleanupPath == path));
        evidence.Fallback = done.Select(d => d.Fallback).FirstOrDefault(f => f is not null);
        evidence.ProviderError = done.Select(d => d.ProviderError).FirstOrDefault(e => e is not null);
        evidence.Engine = done.Select(d => d.Engine).FirstOrDefault(e => e is not null);
        evidence.PromptHash = done.Select(d => d.PromptHash).FirstOrDefault(h => h is not null);
        evidence.ContextUsed = done.Any(d => d.ContextUsed);
        evidence.RejectedOutput = Joined(done.Select(d => d.RejectedOutput), "\n\n");
        evidence.ModelOutput = Joined(done.Select(d => d.ModelOutput), "\n\n");
        evidence.CleanupInput = Joined(done.Select(d => d.CleanupInput), "\n\n");
        evidence.GuardVerdict = Joined(done.Select(d => d.GuardVerdict), ",");
        foreach (var term in done.SelectMany(d => d.Terms)) if (!evidence.Terms.Contains(term)) evidence.Terms.Add(term);
        var texts = results.Select(r => r?.Text ?? "").ToList();
        evidence.Outcome = new ProcessingOutcome.Insert(VoiceCueText.Join(pieces, texts, keepsCase: s => Dictionary.Apply(s) != s));
        return evidence;
    }

    async Task<ProcessingResult> ProcessSegmentAsync(string raw, DictationStyle? style, CancellationToken cancellationToken)
    {
        var evidence = new ProcessingResult(new ProcessingOutcome.Insert(raw));
        style ??= Settings.Style();
        // Writing-mode dictations run on the AI engine; the rest use the cleanup route.
        var cleaner = style.UsesEngine ? EngineCleaner : CloudCleaner;
        // The Local model's Natural cleanup is its one model pass (no levels); other routes use the model for the rewrite styles and presets only.
        var naturalOnLocalModel = !style.UsesEngine && CloudCleaner?.Id == ProviderID.LocalModel && style.Options.AnyEnabled;
        if (!(Settings.Route == CleanupRoute.Cloud || style.UsesEngine) || !(style.UsesModel || naturalOnLocalModel)) return ProcessSegment(raw, style);
        var prepared = Prepare(raw);
        var options = style.Options; var terms = prepared.Terms;
        if (naturalOnLocalModel) options = options with { Polish = true };
        if (cleaner is not null && !cleaner.Id.RunsProfessional()) options = options with { Professional = false };
        if (style.UsesEngine && cleaner is not null) evidence.Engine = cleaner.Id;
        evidence.Terms = terms; evidence.CleanupInput = prepared.Text == raw ? null : prepared.Text;
        var context = style.Context; var instructions = style.Instructions;
        // The provider declares what it takes (CleanupShape): a fixed prompt gets no context or style notes (rewrite presets fall back below),
        // a small window gets capped context.
        if (cleaner?.Shape is { } shape)
        {
            if (!shape.TakesContext) { context = null; instructions = ""; }
            if (!shape.TakesPresets && style.Transform is not null) instructions = "";
            if (shape.ContextCaps is { } caps && context is { } c)
                context = c with { Selected = SwiftText.Prefix(c.Selected, caps.Selected), Clipboard = SwiftText.Prefix(c.Clipboard, caps.Clipboard), Window = SwiftText.Prefix(c.Window, caps.Window) };
        }
        var blank = SwiftText.TrimWhitespaceAndNewlines(prepared.Text).Length == 0;
        if (style.Transform is { } kind && cleaner is not null && !blank)
        {
            if (await TransformCleanAsync(prepared.Text, kind, cleaner, terms, instructions, Probe, cancellationToken) is { } attempt)
            {
                evidence.CleanupPath = Core.CleanupPath.ModelAccepted; evidence.ModelOutput = attempt.Accepted; evidence.PromptHash = attempt.PromptHash;
                evidence.GuardVerdict = attempt.Verdict.RawValue();
                evidence.Outcome = new ProcessingOutcome.Insert(attempt.Text ?? prepared.Text);
                return evidence;
            }
            evidence.Fallback = CleanupFailureReason.CleanerFailed; // Rewrite failed or was rejected: ordinary cleanup below.
        }
        if (!options.AnyEnabled) evidence.CleanupPath = Core.CleanupPath.Off;
        if (!options.AnyEnabled || blank) { evidence.Outcome = new ProcessingOutcome.Insert(Toned(prepared.Text, prepared.Text, style)); return evidence; }
        if (cleaner is not { } provider)
        {
            evidence.Fallback = CleanupFailureReason.CleanerFailed; evidence.CleanupPath = Core.CleanupPath.LocalRules;
            evidence.Outcome = new ProcessingOutcome.Insert(Toned(LocalClean(prepared.Text, terms, options, evidence), prepared.Text, style));
            return evidence;
        }
        // Providers with a slow model or a small window clean long text in parallel sections, each guarded on its own (CleanupShape).
        var providerShape = provider.Shape;
        var sections = providerShape.SectionTarget is { } target ? CleanupSections.Split(prepared.Text, target, providerShape.KeepsCorrectionsAttached) : [prepared.Text];
        evidence.ContextUsed = context is not null;
        var probe = Probe;
        var results = await Task.WhenAll(sections.Select(section =>
            Task.Run(() => CloudCleanAsync(section, provider, options, terms, context, instructions, probe, cancellationToken), CancellationToken.None)));
        // The worst section decides.
        evidence.CleanupPath = results.Any(r => r.Text is null) ? Core.CleanupPath.LocalRules
            : results.Any(r => r.Reason is not null) ? Core.CleanupPath.ModelPunctuationOnLocal : Core.CleanupPath.ModelAccepted;
        var parts = new List<string>();
        for (int i = 0; i < sections.Count; i++)
        {
            var attempt = results[i];
            if (attempt.Reason is { } reason) evidence.Fallback ??= reason;
            if (attempt.Error is { } error) evidence.ProviderError ??= error;
            // A rejected output was never pasted and may echo screen context, so it is not kept for History when context was sent. An accepted
            // output is what was pasted (before tone and spacing), so keeping it stores nothing History does not already hold.
            if (context is null && attempt.Rejected is { } rejected) evidence.RejectedOutput = (evidence.RejectedOutput is { } r0 ? r0 + "\n\n" : "") + rejected;
            if (attempt.Accepted is { } accepted) evidence.ModelOutput = (evidence.ModelOutput is { } m0 ? m0 + "\n\n" : "") + accepted;
            evidence.PromptHash ??= attempt.PromptHash;
            var text = attempt.Text ?? LocalClean(sections[i], terms, options, evidence);
            if (text.Length > 0) parts.Add(text);
        }
        var verdicts = new List<GuardVerdict>();
        foreach (var attempt in results) if (!verdicts.Contains(attempt.Verdict)) verdicts.Add(attempt.Verdict);
        evidence.GuardVerdict = string.Join(",", verdicts.Select(v => v.RawValue()));
        evidence.Outcome = new ProcessingOutcome.Insert(Toned(string.Join(" ", parts), prepared.Text, style));
        return evidence;
    }

    static readonly Regex Nonce = new("-[0-9A-Fa-f]{32}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    static readonly Regex HardBreak = new(@"[ \t]+\n", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>A short, stable name for a system prompt: the template without the per-call tag nonce and without the style notes, screen-context
    /// note and dictionary terms appended after it, so a changed template is the only thing that changes the hash. FNV-1a, 8 hex digits.</summary>
    public static string PromptHash(string system)
    {
        var b = system;
        foreach (var marker in CleanupPrompt.Tails)
            if (b.IndexOf(marker, StringComparison.Ordinal) is var at and >= 0) b = b[..at];
        b = Nonce.Replace(b, "-N");
        ulong hash = 0xcbf29ce484222325;
        foreach (var x in Encoding.UTF8.GetBytes(b)) hash = unchecked((hash ^ x) * 0x100000001b3);
        return hash.ToString("x16")[..8];
    }

    /// <summary>One section's cloud result. Null <see cref="Text"/> means use local rules; <see cref="Reason"/> is why the full cleanup was not used,
    /// <see cref="Error"/> the provider error, and <see cref="Rejected"/> the output the guard refused.</summary>
    public sealed record CloudAttempt(string? Text, CleanupFailureReason? Reason, ProviderError? Error)
    {
        public string? Rejected { get; init; }
        /// <summary>The model's output the guard accepted, as returned.</summary>
        public string? Accepted { get; init; }
        /// <summary>What the guard did with the model output; <c>ProviderError</c> until a result is judged.</summary>
        public GuardVerdict Verdict { get; init; } = GuardVerdict.ProviderError;
        public string? PromptHash { get; init; }
    }

    /// <summary>A preset's rewrite in one provider call, no sections, no tone or cleanup. Null on a provider error or a rejected output.</summary>
    static async Task<CloudAttempt?> TransformCleanAsync(string text, ModeTransform kind, ICleanupProvider provider, IReadOnlyList<string> terms, string instructions,
                                                         Action<string>? probe, CancellationToken cancellationToken)
    {
        CleanupMessages messages;
        try { messages = TransformPrompt.Build(kind, text, terms, instructions: instructions); }
        catch (RegexMatchTimeoutException) { return null; }
        string output;
        try { output = await provider.CleanupAsync(messages, cancellationToken).ConfigureAwait(false); }
        catch (Exception) { return null; } // Swift try?: any failure falls back to ordinary cleanup.
        try
        {
            probe?.Invoke("transform");
            // Spaces before a line break are markdown hard breaks some models add; pasted text never wants them.
            var o = SwiftText.TrimWhitespaceAndNewlines(HardBreak.Replace(output, "\n"));
            if (!TransformGuard.Accepts(text, o)) return null;
            return new CloudAttempt(o, null, null) { Accepted = o, Verdict = GuardVerdict.Transform, PromptHash = PromptHash(messages.System) };
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>One provider call, no retry (a rate limit or an outage would only fail again). If the guard rejects the cleaned words, the
    /// transcript's words take the model's punctuation instead.
    /// The guard judges against the transcript section only. On a trusted route (polish or professional on a cloud or custom model) it holds
    /// only malfunctions, so it does not stop words from screen context reaching the paste: the prompt's data-only tags are the defense there
    /// (spec/pipeline.md §9). The managed local model and Apple Intelligence are always held to the full guard.</summary>
    public static Task<CloudAttempt> CloudCleanAsync(string section, ICleanupProvider provider, CleanupOptions options, IReadOnlyList<string> terms,
                                                    ScreenContext? context = null, string instructions = "", CancellationToken cancellationToken = default) =>
        CloudCleanAsync(section, provider, options, terms, context, instructions, null, cancellationToken);

    static async Task<CloudAttempt> CloudCleanAsync(string section, ICleanupProvider provider, CleanupOptions options, IReadOnlyList<string> terms,
                                                    ScreenContext? context, string instructions, Action<string>? probe, CancellationToken cancellationToken)
    {
        CleanupMessages messages; string hash;
        try
        {
            probe?.Invoke("prompt");
            messages = CleanupPrompt.Build(section, options, terms, context: context, instructions: instructions);
            // The managed model's prompt is fixed inside the helper and Murmur Cloud's is built by the server, so the built prompt's hash would mislead.
            hash = provider.Id == ProviderID.LocalModel ? "local-fixed" : provider.Id == ProviderID.MurmurCloud ? "murmur-cloud-server" : PromptHash(messages.System);
        }
        catch (RegexMatchTimeoutException) { return new CloudAttempt(null, CleanupFailureReason.CleanerFailed, null); }
        string output;
        try { output = await provider.CleanupAsync(messages, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) { return new CloudAttempt(null, CleanupFailureReason.CleanerFailed, (e as ProviderException)?.Error) { PromptHash = hash }; }
        var judged = provider.Id is ProviderID.LocalModel or ProviderID.AppleIntelligence ? options with { TrustModel = false } : options;
        CleanupFailureReason reason;
        try
        {
            probe?.Invoke("judge");
            switch (CleanupPipeline.Judge(section, output, judged, terms, DictionaryStore.SoundsLike))
            {
                case CleanupOutcome.Cleaned c: return new CloudAttempt(c.Text, null, null) { Accepted = output, Verdict = GuardVerdict.Cleaned, PromptHash = hash };
                case CleanupOutcome.Unchanged u: return new CloudAttempt(u.Text, null, null) { Accepted = output, Verdict = GuardVerdict.Unchanged, PromptHash = hash };
                case CleanupOutcome.HeldForRecovery h: reason = h.Reason; break;
                default: throw new InvalidOperationException();
            }
        }
        catch (RegexMatchTimeoutException) { reason = CleanupFailureReason.CleanerFailed; }
        // Transfer punctuation onto the full local cleanup, so rejecting the model's rewrite never skips the enabled local operations.
        try
        {
            var fallback = LocalRules(section, terms, options, new RuleCleaner(), probe).Text;
            probe?.Invoke("punctuated");
            if (options.Punctuation && CleanupPipeline.Punctuated(fallback, output, terms) is { } words
                && CleanupPipeline.Judge(fallback, words, PunctuationOnly, terms) is CleanupOutcome.Cleaned p)
                return new CloudAttempt(p.Text, reason, null) { Rejected = output, Verdict = GuardVerdict.Rejected, PromptHash = hash };
        }
        catch (RegexMatchTimeoutException) { }
        return new CloudAttempt(null, reason, null) { Rejected = output, Verdict = GuardVerdict.Rejected, PromptHash = hash };
    }

    /// <summary>One change to a watched dictated span (the controller polls <see cref="DictationObservation"/>). An edit becomes provisional
    /// evidence; back to the original (undo) drops this session's evidence. Call <see cref="Settle"/> when the window closes.
    /// Returns what the ledger made of it, or null when the span went back to the original.</summary>
    public CorrectionLedger.RecordOutcome? ObserveSpan(Guid sessionId, string original, string current)
    {
        // The ledger diffs the span word by word, so punctuation and unchanged words never reach the filter.
        if (current == original) { Ledger.Revert(sessionId); return null; }
        return Observe(new ObservedCorrection { SessionId = sessionId, Source = original, Target = current, ObservedAt = DateTime.UtcNow });
    }

    /// <summary>Feeds one attributed correction while its observation window is open. With learning off nothing is recorded.</summary>
    public CorrectionLedger.RecordOutcome Observe(ObservedCorrection correction) =>
        Settings.LearningEnabled ? Ledger.Record(correction) : CorrectionLedger.RecordOutcome.LearningDisabled;

    public IReadOnlyList<DictionaryEntry> Settle(Guid sessionId) => Ledger.Settle(sessionId);

    /// <summary>Variants are comma-separated; blanks are dropped.</summary>
    static List<string> VariantList(string variants) =>
        variants.Split(',').Select(Strings.TrimWhitespaces).Where(v => v.Length > 0).ToList();

    public DictionaryEntry AddEntry(string preferred, string variants) => Dictionary.Add(preferred, VariantList(variants), EntrySource.Manual);

    /// <summary>Edits an entry in place; a learned entry stays learned. Its ledger promotion is keyed by entry ID, so old evidence cannot
    /// re-promote the original spelling alongside the edit.</summary>
    public void UpdateEntry(Guid id, string preferred, string variants, bool enabled)
    {
        var entry = Dictionary.Entries.FirstOrDefault(e => e.Id == id) ?? throw new StoreError("Dictionary entry not found");
        Dictionary.Update(entry with { Preferred = preferred, Variants = VariantList(variants), Enabled = enabled });
    }

    /// <summary>A learned entry is tombstoned so old evidence cannot re-promote it.</summary>
    public void DeleteEntry(Guid id)
    {
        if (Dictionary.Entries.FirstOrDefault(e => e.Id == id)?.Source == EntrySource.Learned) Ledger.DeleteLearned(id);
        else Dictionary.Delete(id);
    }

    /// <summary>Removes learned entries and all learning evidence; manual entries stay.</summary>
    public void ResetLearned() => Ledger.Reset();
}

/// <summary>What the guard did with a cleanup model's output. Stored in History as the raw value, comma-joined per section.</summary>
public enum GuardVerdict { Cleaned, Unchanged, Rejected, ProviderError, Transform }

public static class GuardVerdictExtensions
{
    public static string RawValue(this GuardVerdict verdict) => verdict switch
    {
        GuardVerdict.Cleaned => "cleaned",
        GuardVerdict.Unchanged => "unchanged",
        GuardVerdict.Rejected => "rejected",
        GuardVerdict.ProviderError => "provider-error",
        _ => "transform",
    };
}
