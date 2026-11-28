// The path for replacing the local cleanup model (windows/CLEANUP-MODEL-SWAP.md): capabilities come from the profile, and a retired model's folder is cleaned up.
using Murmur.Core;
using static Murmur.Core.Tests.ModelTestSupport;

namespace Murmur.Core.Tests;

/// <summary>A profile with the Mac's Murmur Cleanup 2B capabilities, for the tests that compare with the Mac's goldens and behavior.</summary>
public static class MacLikeModel
{
    public static readonly LocalCleanupModel Profile = new()
    {
        Name = "Murmur Cleanup 2B", ModelId = "clouddevaakash/murmur-cleanup-2b@b591570", Repository = "clouddevaakash/murmur-cleanup-2b-gguf", FolderName = "murmur-cleanup-2b",
        RetiredFolderNames = ["vedu-scribe-0.8b"], RunsModelLevels = true, RunsProfessional = true, TakesContextAndPresets = true, TrustedLikeCloud = true, MaximumCharacters = 4_000,
    };
    public static IDisposable Use() => LocalCleanupModels.Use(Profile);
    /// <summary>The tests ported from the Mac (and its goldens) describe the Mac's model; the swap tests choose theirs with <see cref="LocalCleanupModels.Use"/>.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SetTestDefault() => LocalCleanupModels.TestDefault = Profile;
}

public class LocalCleanupModelSwapTests
{
    static async Task<ProviderError?> Failure(Func<Task> action)
    {
        try { await action(); return null; } catch (ProviderException e) { return e.Error; }
    }

    sealed class Recorder { public readonly List<LocalCleanupRequest> Requests = []; }
    static LocalModelCleanupProvider Provider(Recorder rec) => new()
    {
        StatusSource = () => null,
        Generate = (request, _) => { rec.Requests.Add(request); return Task.FromResult(request.Text); },
    };

    [Fact]
    public void ShippedModelKeepsTheEarlierBehavior()
    {
        Assert.Same(LocalCleanupModels.VeduScribe08B, LocalCleanupModels.Installed);
        using var _ = LocalCleanupModels.Use(LocalCleanupModels.Installed);
        Assert.Same(LocalCleanupModels.VeduScribe08B, LocalCleanupModels.Current);
        Assert.False(CleanupChoice.LocalModel.RunsModelLevels());
        Assert.False(ProviderID.LocalModel.RunsProfessional());
        var shape = Provider(new()).Shape;
        Assert.False(shape.TakesContext || shape.TakesPresets);
        Assert.Equal(CleanupChoice.CustomAPI, CleanupStyles.Select(CleanupStyle.Polish, CleanupChoice.LocalRules, CleanupOptions.Standard).Route);
    }

    [Fact]
    public async Task ACapableModelTakesLevelsPresetsAndTone()
    {
        using var _ = LocalCleanupModels.Use(MacLikeModel.Profile);
        Assert.True(CleanupChoice.LocalModel.RunsModelLevels());
        Assert.False(CleanupChoice.LocalRules.RunsModelLevels());
        Assert.True(ProviderID.LocalModel.RunsProfessional());
        Assert.Equal(CleanupChoice.LocalModel, CleanupStyles.Select(CleanupStyle.Professional, CleanupChoice.LocalRules, CleanupOptions.Standard).Route);
        var rec = new Recorder();
        var provider = Provider(rec);
        Assert.True(provider.Shape.TakesContext && provider.Shape.TakesPresets);
        await provider.CleanupAsync(TransformPrompt.Build(ModeTransform.Email, "send the report", ["Quarterly"]));
        Assert.Equal("email", rec.Requests[0].Mode);
        Assert.Equal("send the report", rec.Requests[0].Text);
        await provider.CleanupAsync(CleanupPrompt.Build("hello there", CleanupOptions.Standard with { Polish = true, Professional = true }, context: new ScreenContext { Selected = "a [b]\n c", Window = "w" }));
        Assert.Equal("formal", rec.Requests[1].Tone);
        Assert.Equal("a (b) c | w", rec.Requests[1].Screen);
    }

    [Fact]
    public async Task AModelWithoutPresetsRefusesThem()
    {
        using var _ = LocalCleanupModels.Use(LocalCleanupModels.Installed);
        var rec = new Recorder();
        var failure = await Failure(() => Provider(rec).CleanupAsync(TransformPrompt.Build(ModeTransform.Email, "send the report")));
        Assert.Equal(ProviderError.InvalidConfiguration(ProviderID.LocalModel), failure);
        Assert.Empty(rec.Requests);
    }

    [Fact]
    public void ReadyRowOffersReinstall() =>
        Assert.Equal([ModelRowAction.Reinstall, ModelRowAction.Remove], ModelRowPresentation.Make(ModelStatus.Ready).Actions);

    [Fact]
    public void RetiredFoldersAreDeletedOnceTheNewModelIsReady()
    {
        var root = Path.Combine(Path.GetTempPath(), "murmur-retired-" + Guid.NewGuid().ToString("N"));
        try
        {
            var models = ModelFacts.ModelsRoot(root);
            var old = Directory.CreateDirectory(Path.Combine(models, "vedu-scribe-0.8b-edf2759")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(models, "vedu-scribe-0.8b-notes")).FullName; // not <name>-<7 hex>: kept
            File.WriteAllText(Path.Combine(old, "model.gguf"), "x");
            var facts = ModelFacts.LocalCleanup with { Retired = ["vedu-scribe-0.8b"] };
            var slot = new ModelSlot(facts, null, root);
            Assert.Equal([old], slot.RetiredFolders());
            slot.ReconcileRetired();
            Assert.Contains("newer model", slot.Message);
            Assert.True(Directory.Exists(old));
            slot.Status = ModelStatus.Ready;
            slot.ReconcileRetired();
            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(other));
            // A swap that lists its own folder name as retired never deletes the model it just installed.
            var current = ModelFacts.LocalCleanup with { Retired = [LocalCleanupModels.Installed.FolderName] };
            var installed = ModelSlot.Create(current, root);
            Directory.CreateDirectory(installed.Downloader!.Target);
            Assert.DoesNotContain(installed.Downloader.Target, installed.RetiredFolders());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
