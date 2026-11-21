// The spoken mode triggers as Parakeet actually writes them (Windows SAPI speech through the bundled Murmur.Speech helper, 2026-11-21),
// and the post-transcription path the app takes: trigger -> mode -> engine rewrite, or a notice when there is no engine.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class SpokenModeTests
{
    static readonly List<WritingMode> Modes = [.. WritingMode.Presets];

    // Exact Parakeet outputs (casing and punctuation as produced).
    [Theory]
    [InlineData("Prompt mode. Write a haiku about rain", ModeTransform.AiPrompt, "Write a haiku about rain")]
    [InlineData("Prompt mode, write a haiku about rain.", ModeTransform.AiPrompt, "Write a haiku about rain.")]
    [InlineData("Prompt mode write a haiku about rain", ModeTransform.AiPrompt, "Write a haiku about rain")]
    [InlineData("Prompt engineer. Write a haiku about rain.", ModeTransform.AiPrompt, "Write a haiku about rain.")]
    [InlineData("Email mode, tell Sam I'm late.", ModeTransform.Email, "Tell Sam I'm late.")]
    [InlineData("Email mode. Tell Sam I'm late.", ModeTransform.Email, "Tell Sam I'm late.")]
    [InlineData("E-mail mode, tell Sam I'm late.", ModeTransform.Email, "Tell Sam I'm late.")]
    [InlineData("email mode tell sam I'm late", ModeTransform.Email, "tell sam I'm late")]
    [InlineData("Prompt Mode, write a haiku about rain.", ModeTransform.AiPrompt, "Write a haiku about rain.")]
    [InlineData("Notes mode. Buy milk and eggs.", ModeTransform.Notes, "Buy milk and eggs.")]
    public void ParakeetOutputsFireTheirPreset(string transcript, ModeTransform kind, string text)
    {
        var said = WritingModes.Trigger(Modes, transcript);
        Assert.NotNull(said);
        Assert.Equal(kind, said!.Value.Mode.Transform);
        Assert.Equal(text, said.Value.Text);
    }

    [Theory]
    [InlineData("Write a haiku about rain. Prompt mode")]  // presets match at the start only
    [InlineData("Tell Sam I'm late, email mode.")]
    [InlineData("Prompt mode")]
    public void OtherPhrasingsDoNotFire(string transcript) => Assert.Null(WritingModes.Trigger(Modes, transcript));

    [Fact]
    public void HyphenatedCustomTriggerStillMatchesEitherSpelling()
    {
        Assert.Equal("Fix it", WritingModes.StripTrigger("e-mail mode", "Email mode, fix it"));
        Assert.Equal("Fix it", WritingModes.StripTrigger("email mode", "E-mail mode, fix it"));
    }

    [Fact]
    public void PresetsMatchTheMacTriggerWords()
    {
        Assert.Equal(["prompt mode", "prompt engineer"], WritingMode.Presets.Single(m => m.Transform == ModeTransform.AiPrompt).Triggers);
        Assert.Equal(["email mode"], WritingMode.Presets.Single(m => m.Transform == ModeTransform.Email).Triggers);
        Assert.Equal(["notes mode"], WritingMode.Presets.Single(m => m.Transform == ModeTransform.Notes).Triggers);
    }

    [Fact]
    public async Task SpokenPromptModeRewritesThroughTheEngine()
    {
        var p = new ProcessingCoordinator(TempDirs.New("spoken-mode"), new InMemorySettingsStore());
        var said = WritingModes.Trigger(Modes, "Prompt mode. Write a haiku about rain")!.Value;
        var engine = MockProvider.Fixed("Write a three-line haiku about rain, in 5-7-5 syllables, with a quiet tone.");
        p.EngineCleaner = engine;
        var result = await p.ProcessAsync(said.Text, p.Settings.Style(said.Mode));
        Assert.Single(engine.Messages);
        Assert.Contains("Write a haiku about rain", engine.Messages[0].User);
        Assert.DoesNotContain("Prompt mode", engine.Messages[0].User);
        Assert.Equal("Write a three-line haiku about rain, in 5-7-5 syllables, with a quiet tone.", result.Text);
        Assert.Null(ModeFeedback.Notice(said.Mode, hasEngine: true, result.Fallback));
    }

    [Fact]
    public async Task SpokenEmailModeWithoutAnEngineSaysSo()
    {
        var p = new ProcessingCoordinator(TempDirs.New("spoken-mode-none"), new InMemorySettingsStore());
        var said = WritingModes.Trigger(Modes, "Email mode, tell Sam I'm late.")!.Value;
        var result = await p.ProcessAsync(said.Text, p.Settings.Style(said.Mode));
        Assert.Equal(CleanupFailureReason.CleanerFailed, result.Fallback);
        Assert.Contains("Murmur Cloud or a Custom Provider", ModeFeedback.Notice(said.Mode, hasEngine: false, result.Fallback));
    }

    [Fact]
    public void NoticeOnlyForRewriteModes()
    {
        Assert.Null(ModeFeedback.Notice(null, false, null));
        Assert.Null(ModeFeedback.Notice(new WritingMode { Name = "Slack" }, false, CleanupFailureReason.CleanerFailed));
        var email = WritingMode.Presets.Single(m => m.Transform == ModeTransform.Email);
        Assert.Contains("could not rewrite", ModeFeedback.Notice(email, true, CleanupFailureReason.CleanerFailed));
        Assert.Null(ModeFeedback.Notice(email, true, null));
    }
}
