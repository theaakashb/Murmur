// Port of app/ModelText.swift.
// Model row text and rules: every downloadable model's facts, the row's buttons and the download/remove gate.
using System.Globalization;

namespace Murmur.Core;

/// <summary>Which pins file a model's entry lives in.</summary>
public enum ModelPinsSource
{
    /// <summary><c>module-one/models/artifact-pins.json</c>, shared with the Mac and copied beside Murmur.exe.</summary>
    ArtifactPins,
    /// <summary><c>windows/shared/windows-models.json</c>, embedded in Murmur.Core: models only Windows uses, including staged (not yet uploaded) ones.</summary>
    WindowsModels,
}

/// <summary>One downloadable model: where it comes from, where it lives and what Settings says about it. The text layouts (size, summary, confirmation, completion)
/// are written once below; a model's own wording is a field here. Not ported: the Mac's dictionary-support (CTC) and speaker models, which Windows replaces differently.</summary>
public sealed partial record ModelFacts
{
    /// <summary>The model's name where it is a setting or a step.</summary>
    public required string DisplayName { get; init; }
    /// <summary>The row's title when it differs from <see cref="DisplayName"/>.</summary>
    public string? RowTitle { get; init; }
    public required string Repository { get; init; }
    public string List { get; init; } = ModelManifest.BatchList;
    public ModelPinsSource Source { get; init; } = ModelPinsSource.ArtifactPins;
    /// <summary>The folder's name before <c>-&lt;rev7&gt;</c>; the repository's name when null.</summary>
    public string? FolderName { get; init; }
    /// <summary>The license the model cards declare when the pins record none.</summary>
    public ModelLicenseInfo? UnpinnedLicense { get; init; }
    /// <summary>The model's name in "Download …?".</summary>
    public required string ConfirmName { get; init; }
    /// <summary>Leads the summary line ahead of the size.</summary>
    public string? SummaryLead { get; init; }
    /// <summary>Summary items between the size and where it runs.</summary>
    public IReadOnlyList<string> SummaryTail { get; init; } = [];
    /// <summary>The sentence after the checksum sentence in the confirmation.</summary>
    public required Func<string, string> Placement { get; init; }
    /// <summary>Replaces the "License:" block.</summary>
    public string? LicenseNotice { get; init; }
    /// <summary>A final paragraph of the confirmation.</summary>
    public string? Closing { get; init; }
    /// <summary>A paragraph worked out when the confirmation is shown (null for none): Windows' notes model warns here when no GPU can run it.</summary>
    public Func<string?>? ClosingSource { get; init; }
    public required string RemoveTitle { get; init; }
    public required Func<string, long, string> RemoveBody { get; init; }
    public required string Completed { get; init; }
    public required string Removed { get; init; }
    public string Explanation { get; init; } = "";
    public string UnavailableMessage { get; init; } = "";

    public const string Location = "Runs on this PC";

    public string Title => RowTitle ?? DisplayName;

    /// <summary><c>&lt;data root&gt;\Models</c>: every downloaded model, kept by Delete All.</summary>
    public static string ModelsRoot(string dataRoot) => Path.Combine(dataRoot, "Models");

    /// <summary><c>&lt;data root&gt;\Models\&lt;model&gt;-&lt;rev7&gt;</c>.</summary>
    public string Directory(string dataRoot, ModelManifest manifest) =>
        Path.Combine(ModelsRoot(dataRoot), $"{FolderName ?? Repository[(Repository.IndexOf('/') + 1)..]}-{manifest.Revision[..7]}");

    public ModelManifest Manifest(byte[] pins) => new(pins, Repository, List, UnpinnedLicense);

    /// <summary>Decimal units, as Explorer shows download sizes: 1,801,783,964 bytes is "1.80 GB".</summary>
    public static string FormatBytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        if (value >= 1_000_000_000) return (value / 1_000_000_000).ToString("F2", CultureInfo.InvariantCulture) + " GB";
        if (value >= 1_000_000) return Math.Round(value / 1_000_000, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture) + " MB";
        if (value >= 1_000) return Math.Round(value / 1_000, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture) + " KB";
        return $"{(long)value} bytes";
    }

    /// <summary>"1.80 GB download · English · Runs on this PC"</summary>
    public string Summary(long downloadSize) =>
        string.Join(" · ", (SummaryLead is null ? [] : new[] { SummaryLead }).Concat([$"{FormatBytes(downloadSize)} download"]).Concat(SummaryTail).Concat([Location]));

    /// <summary>One line per declaration, with its link, then the note (which says when the license is unresolved).</summary>
    public static string LicenseText(ModelLicenseInfo license) =>
        string.Join("\n", license.Declarations.Select(d => $"{d.Name} ({d.Source}): {d.Url.AbsoluteUri}").Append(license.Note));

    /// <summary>Shown before any byte is downloaded.</summary>
    public (string Title, string Body) Confirmation(long downloadSize, ModelLicenseInfo license, string folder)
    {
        var size = FormatBytes(downloadSize);
        return ($"Download {ConfirmName} ({size})?",
                $"Murmur downloads {size} from Hugging Face and checks every file against its pinned checksum. {Placement(folder)}\n\n{LicenseNotice ?? $"License:\n{LicenseText(license)}"}" + ((Closing ?? ClosingSource?.Invoke()) is { } closing ? "\n\n" + closing : ""));
    }

    public static string SavedIn(string folder) => $"It is saved in {folder}.";

    public string CompletionMessage(ModelDownloadError? error) => error is { } failure ? FailureMessage(failure) : Completed;

    public static string FailureMessage(ModelDownloadError error) =>
        error == ModelDownloadError.Cancelled ? "The download was cancelled. Retry continues from the files already downloaded." : error.UserMessage;

    const string NvidiaLicenseUrl = "https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/";

    /// <summary>The batch transcription model: NVIDIA Parakeet Unified English 0.6B as sherpa-onnx int8 ONNX files (PLAN.md "Speech recognition").
    /// The NVIDIA Open Model License is recorded in the pins, so it shows as pinned.</summary>
    public static readonly ModelFacts Batch = new()
    {
        DisplayName = "Parakeet Unified English 0.6B",
        Repository = "csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming",
        ConfirmName = "Parakeet Unified English 0.6B",
        SummaryTail = ["English"],
        Placement = _ => "After that, transcription runs on this PC without the network.",
        RemoveTitle = "Remove the downloaded model?",
        RemoveBody = (_, size) => $"Murmur cannot dictate until you download it again ({FormatBytes(size)}) or choose another model folder.",
        Completed = "The model is downloaded and verified. Murmur is ready to dictate.",
        Removed = "The model was removed.",
    };

    /// <summary>Live words: the streaming encoder (560 ms chunks) in a folder of its own beside the batch model. Optional; dictation never needs it.</summary>
    public static readonly ModelFacts LiveWords = new()
    {
        DisplayName = "Live words while speaking",
        RowTitle = "Parakeet Unified streaming",
        Repository = "csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-streaming-560ms",
        ConfirmName = "live words",
        SummaryLead = "Parakeet Unified streaming",
        Placement = SavedIn,
        RemoveTitle = "Remove live words?",
        RemoveBody = (folder, _) => $"The bubble goes back to the level meter until you download it again. This deletes {folder}.",
        Completed = "Live words are downloaded and verified.",
        Removed = "Live words were removed.",
        Explanation = "Shows your words in the bubble while you speak. Display only: the inserted text still comes from the whole recording. While you record, a second on-device model uses extra memory; it quits when you stop.",
    };
}

/// <summary>What the local speech helper needs to know about each of the three models (DictationLanguages.LocalModel in app/ModelText.swift).</summary>
public static class LocalSpeechModels
{
    /// <summary>The helper's runner flag; null for the English path.</summary>
    public static string? RunnerFlag(this LocalSpeechModel model) => model switch
    {
        LocalSpeechModel.ParakeetEnglish => null,
        LocalSpeechModel.ParakeetTDT => "--parakeet-tdt",
        _ => "--whisper-turbo",
    };

    public static ModelFacts Facts(this LocalSpeechModel model) => model switch
    {
        LocalSpeechModel.ParakeetEnglish => ModelFacts.Batch,
        LocalSpeechModel.ParakeetTDT => ModelFacts.MultilingualTDT,
        _ => ModelFacts.MultilingualWhisper,
    };

    /// <summary>The files the helper opens in the model's folder.</summary>
    public static IReadOnlyList<string> RequiredFiles(this LocalSpeechModel model) => model == LocalSpeechModel.WhisperTurbo
        ? ["turbo-encoder.int8.onnx", "turbo-decoder.int8.onnx", "turbo-tokens.txt"]
        : ModelHelper.RequiredFiles;
}

public sealed partial record ModelFacts
{
    /// <summary>Parakeet TDT v3 (25 European languages) as sherpa-onnx int8 files. Its pin is not in windows-models.json until the model is approved, so its slot has no downloader.</summary>
    public static readonly ModelFacts MultilingualTDT = Multilingual("Parakeet TDT v3", "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "parakeet-tdt-v3",
        new ModelLicenseInfo(false,
        [
            new("NVIDIA upstream model card", "CC BY 4.0", new("https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3")),
            new("sherpa-onnx conversion model card", "CC BY 4.0", new("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/blob/ed5d745623472f97cfc2e6c2916ee248798fc5a8/README.md")),
        ], "Review the upstream and conversion license declarations before downloading."));

    /// <summary>Whisper large-v3-turbo int8 (about 100 languages); its MIT license is recorded in the pins.</summary>
    public static readonly ModelFacts MultilingualWhisper = Multilingual("Whisper large-v3-turbo", "csukuangfj/sherpa-onnx-whisper-turbo", "whisper-large-v3-turbo", null);

    static ModelFacts Multilingual(string name, string repository, string folder, ModelLicenseInfo? license) => new()
    {
        DisplayName = name, Repository = repository, Source = ModelPinsSource.WindowsModels, FolderName = folder,
        UnpinnedLicense = license, ConfirmName = name,
        Placement = _ => "After that, transcription runs on this PC without the network.",
        RemoveTitle = "Remove the downloaded model?",
        RemoveBody = (_, size) => $"Download it again ({FormatBytes(size)}) to dictate with these languages.",
        Completed = "The model is downloaded and verified.", Removed = "The model was removed.",
    };
}

/// <summary>One model's download state beside its downloader (null when the pins are missing or invalid).</summary>
public sealed class ModelSlot
{
    public ModelFacts Facts { get; }
    public ModelDownloader? Downloader { get; }
    public ModelManifest? Manifest { get; }
    public ModelStatus Status { get; set; } = ModelStatus.NotDownloaded;
    public string Message { get; set; } = "";
    public bool InFlight { get; set; }

    /// <param name="pins">the bytes of the pins file named by <see cref="ModelFacts.Source"/>; null or invalid leaves no downloader.</param>
    public ModelSlot(ModelFacts facts, byte[]? pins, string dataRoot, IModelDownloadTransport? transport = null)
    {
        Facts = facts;
        if (pins is null) return;
        try { Manifest = facts.Manifest(pins); }
        catch (ModelDownloadException) { return; }
        Downloader = new ModelDownloader(Manifest, facts.Directory(dataRoot, Manifest), transport);
    }

    /// <summary>A slot over the bundled pins for <paramref name="facts"/>.</summary>
    public static ModelSlot Create(ModelFacts facts, string dataRoot) => new(facts, BundledModels.Pins(facts.Source), dataRoot);

    public ModelRowPresentation Row => ModelRowPresentation.Make(Downloader is null ? null : Status);
    public ModelLicenseInfo? License => Downloader?.License;
    public string Summary => Downloader is null ? "" : Facts.Summary(Downloader.DownloadSize);
}

/// <summary>Live words: when a recording starts the helper, and the bubble's text.</summary>
public static class LiveWordsFacts
{
    /// <summary>Whether a recording starts the live-words helper. On a cloud transcription route it starts only with "Show live words on cloud routes" on (off by default); the preview itself is always on this PC.</summary>
    public static bool Starts(bool enabled, bool ready, bool cloudTranscription, bool onCloud) => enabled && ready && (!cloudTranscription || onCloud);

    /// <summary>The bubble's text: everything but the last word is settled; the last word may still grow ("onboard" → "onboarding"), so it is shown dimmed. Only the tail that fits two lines is kept.</summary>
    public static (string Settled, string Guess) Split(string text, int limit = 140)
    {
        var tail = SwiftText.Characters(SwiftText.TrimWhitespaceAndNewlines(text));
        if (tail.Count > limit)
        {
            var space = tail.FindIndex(tail.Count - limit, c => c == " ");
            if (space >= 0) tail = ["…", .. tail.Skip(space)];
        }
        var last = tail.FindLastIndex(c => c == " ");
        if (last < 0) return ("", string.Concat(tail));
        return (string.Concat(tail.Take(last + 1)), string.Concat(tail.Skip(last + 1)));
    }
}

public enum ModelRowAction { Download, Cancel, Retry, Remove }

public sealed record ModelRowPresentation(string StatusText, IReadOnlyList<ModelRowAction> Actions, double? Progress, bool IsWorking, bool IsReady)
{
    public bool Equals(ModelRowPresentation? other) =>
        other is not null && StatusText == other.StatusText && Actions.SequenceEqual(other.Actions) && Progress == other.Progress && IsWorking == other.IsWorking && IsReady == other.IsReady;
    public override int GetHashCode() => HashCode.Combine(StatusText, Actions.Count, Progress, IsWorking, IsReady);

    /// <summary><paramref name="status"/> is null when the bundled manifest is missing or invalid.</summary>
    public static ModelRowPresentation Make(ModelStatus? status)
    {
        if (status is null) return new(ModelDownloadError.InvalidManifest.UserMessage, [], null, false, false);
        switch (status.Kind)
        {
            case ModelStatusKind.NotDownloaded: return new("Not downloaded", [ModelRowAction.Download], null, false, false);
            case ModelStatusKind.Downloading:
                var progress = status.Progress!.Value;
                return new($"Downloading… {(int)Math.Floor(progress.Fraction * 100)}% ({ModelFacts.FormatBytes(progress.BytesDone)} of {ModelFacts.FormatBytes(progress.TotalBytes)})", [ModelRowAction.Cancel], progress.Fraction, true, false);
            case ModelStatusKind.Verifying: return new("Verifying downloaded files…", [ModelRowAction.Cancel], null, true, false);
            case ModelStatusKind.Ready: return new("Downloaded", [ModelRowAction.Remove], null, false, true);
            case ModelStatusKind.Corrupt: return new(status.Reason!, [ModelRowAction.Retry, ModelRowAction.Remove], null, false, false);
            default: return new(ModelFacts.FailureMessage(status.Error!.Value), [ModelRowAction.Retry, ModelRowAction.Remove], null, false, false);
        }
    }
}

/// <summary>Content-free refusals. Each returns null when the action may proceed.</summary>
public static class ModelGate
{
    public const string DownloadingMessage = "The model is still downloading. Wait for it to finish, or cancel it in Settings.";
    public const string MissingRuntimeMessage = "The local model runtime is missing from this build. Reinstall Murmur.";
    public const string MissingModelMessage = "The transcription model is not available. Download the model in Settings, or choose a downloaded model folder.";

    /// <param name="downloadingActiveModel">a download is replacing the folder this dictation would read.</param>
    public static string? StartRefusal(bool modelAvailable, bool runtimeAvailable, bool downloadingActiveModel)
    {
        if (downloadingActiveModel) return DownloadingMessage;
        if (!runtimeAvailable) return MissingRuntimeMessage;
        if (!modelAvailable) return MissingModelMessage;
        return null;
    }

    public static string? DownloadRefusal(bool isBusy, bool preview, bool downloading)
    {
        if (preview) return "Model download is disabled in preview.";
        if (downloading) return ModelDownloadError.Busy.UserMessage;
        if (isBusy) return "Finish the current dictation before downloading the model.";
        return null;
    }

    public static string? RemoveRefusal(bool isBusy, bool preview, bool downloading)
    {
        if (preview) return "Removing the model is disabled in preview.";
        if (downloading) return "Cancel the download before removing the model.";
        if (isBusy) return "Finish the current dictation before removing the model.";
        return null;
    }
}
