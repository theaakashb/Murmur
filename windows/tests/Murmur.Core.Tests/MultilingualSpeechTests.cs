// The multilingual speech code paths (PR #102): HelperLaunch flags and arguments, the models' facts, and the missing-pin behaviour. No real model is used.
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class MultilingualSpeechTests
{
    static string Dir(string name) => Directory.CreateDirectory(TempDirs.New(name)).FullName;

    /// <summary>A model folder holding the model's files, and a manifest that pins exactly those bytes under the model's repository.</summary>
    static (string Folder, ModelManifest Manifest) Pinned(LocalSpeechModel model)
    {
        var folder = Dir("multilingual");
        var files = new JsonArray();
        var batch = new JsonArray();
        foreach (var name in model.RequiredFiles())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("content of " + name);
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            files.Add(new JsonObject { ["rfilename"] = name, ["size"] = bytes.Length, ["lfs"] = new JsonObject { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["size"] = bytes.Length } });
            batch.Add(name);
        }
        var repository = model.Facts().Repository;
        var json = new JsonObject { ["models"] = new JsonObject { [repository] = new JsonObject { ["revision"] = new string('a', 40), ["files"] = files, ["batch_files"] = batch } } };
        return (folder, new ModelManifest(json.ToJsonString(), repository));
    }

    [Theory]
    [InlineData(LocalSpeechModel.ParakeetEnglish, null)]
    [InlineData(LocalSpeechModel.ParakeetTDT, "--parakeet-tdt")]
    [InlineData(LocalSpeechModel.WhisperTurbo, "--whisper-turbo")]
    public void EachModelHasItsRunnerFlag(LocalSpeechModel model, string? flag) => Assert.Equal(flag, model.RunnerFlag());

    [Fact]
    public void EnglishLaunchKeepsItsDictionaryArgumentsAndNoFlag()
    {
        var models = Dir("english");
        foreach (var f in ModelHelper.RequiredFiles) File.WriteAllText(Path.Combine(models, f), f);
        var vocabulary = Path.Combine(Dir("vocab"), "vocabulary.json");
        File.WriteAllText(vocabulary, "{\"terms\":[{\"text\":\"Acme\"}]}");
        var launch = HelperLaunch.Make(models, vocabulary)!;
        Assert.Null(launch.RunnerFlag);
        Assert.Equal(LocalSpeechModel.ParakeetEnglish, launch.Model);
        Assert.Equal(["--vocabulary", vocabulary], launch.Arguments);
        Assert.Equal(launch.DictionaryArguments, launch.Arguments);
        Assert.True(launch.HasVocabulary);
    }

    [Theory]
    [InlineData(LocalSpeechModel.ParakeetTDT, "--parakeet-tdt")]
    [InlineData(LocalSpeechModel.WhisperTurbo, "--whisper-turbo")]
    public void MultilingualLaunchIsTheFlagAloneAndTakesNoDictionary(LocalSpeechModel model, string flag)
    {
        var (folder, manifest) = Pinned(model);
        var vocabulary = Path.Combine(Dir("vocab"), "vocabulary.json");
        File.WriteAllText(vocabulary, "{\"terms\":[{\"text\":\"Acme\"}]}");
        var launch = HelperLaunch.Make(folder, vocabulary, lexiconFile: null, model: model, manifest: manifest)!;
        Assert.Equal(model, launch.Model);
        Assert.Equal(flag, launch.RunnerFlag);
        Assert.Equal([flag], launch.Arguments);
        Assert.Empty(launch.DictionaryArguments);
        Assert.False(launch.HasVocabulary);
        Assert.Contains(flag, launch.Key);
        // The key follows the model files, so replacing one restarts the warm helper.
        File.WriteAllText(Path.Combine(folder, model.RequiredFiles()[0]), "changed");
        Assert.Null(HelperLaunch.Make(folder, model: model, manifest: manifest)); // a changed file no longer verifies
    }

    [Fact]
    public void MultilingualLaunchFailsCleanlyWithoutPinsOrFiles()
    {
        var (folder, manifest) = Pinned(LocalSpeechModel.ParakeetTDT);
        Assert.Null(HelperLaunch.Make(folder, model: LocalSpeechModel.ParakeetTDT, manifest: null)); // no pins: the model is not available
        Assert.Null(HelperLaunch.Make(Path.Combine(Dir("empty"), "none"), model: LocalSpeechModel.ParakeetTDT, manifest: manifest)); // pinned files missing
        Assert.NotNull(HelperLaunch.Make(folder, model: LocalSpeechModel.ParakeetTDT, manifest: manifest));
    }

    [Fact]
    public void WhisperNeedsItsOwnThreeFiles()
    {
        Assert.Equal(["turbo-encoder.int8.onnx", "turbo-decoder.int8.onnx", "turbo-tokens.txt"], LocalSpeechModel.WhisperTurbo.RequiredFiles());
        Assert.Equal(ModelHelper.RequiredFiles, LocalSpeechModel.ParakeetTDT.RequiredFiles());
        Assert.Equal(ModelHelper.RequiredFiles, LocalSpeechModel.ParakeetEnglish.RequiredFiles());
    }

    [Fact]
    public void FactsNameTheWindowsRepositoriesAndFolders()
    {
        Assert.Same(ModelFacts.Batch, LocalSpeechModel.ParakeetEnglish.Facts());
        var tdt = ModelFacts.MultilingualTDT;
        Assert.Same(tdt, LocalSpeechModel.ParakeetTDT.Facts());
        Assert.Equal("Parakeet TDT v3", tdt.DisplayName);
        Assert.Equal("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", tdt.Repository);
        Assert.Equal("parakeet-tdt-v3", tdt.FolderName);
        var whisper = ModelFacts.MultilingualWhisper;
        Assert.Same(whisper, LocalSpeechModel.WhisperTurbo.Facts());
        Assert.Equal("Whisper large-v3-turbo", whisper.DisplayName);
        Assert.Equal("csukuangfj/sherpa-onnx-whisper-turbo", whisper.Repository);
        Assert.Equal("whisper-large-v3-turbo", whisper.FolderName);
        Assert.All(new[] { tdt, whisper }, f => Assert.Equal(ModelPinsSource.WindowsModels, f.Source));
        // The folder sits under the Windows models directory, named by its pinned revision.
        var (_, manifest) = Pinned(LocalSpeechModel.ParakeetTDT);
        Assert.Equal(Path.Combine(ModelFacts.ModelsRoot(@"T:\data"), "parakeet-tdt-v3-aaaaaaa"), tdt.Directory(@"T:\data", manifest));
    }

    [Fact]
    public void FactsCarryTheMacCopyForThisPc()
    {
        foreach (var facts in new[] { ModelFacts.MultilingualTDT, ModelFacts.MultilingualWhisper })
        {
            Assert.Equal("After that, transcription runs on this PC without the network.", facts.Placement(@"T:\x"));
            Assert.Equal("Remove the downloaded model?", facts.RemoveTitle);
            Assert.Equal("Download it again (1.80 GB) to dictate with these languages.", facts.RemoveBody(@"T:\x", 1_801_783_964));
            Assert.Equal("The model is downloaded and verified.", facts.Completed);
            Assert.Equal("The model was removed.", facts.Removed);
            Assert.Equal(facts.DisplayName, facts.ConfirmName);
            Assert.Contains("on this PC", facts.Summary(1_000_000));
        }
    }

    [Fact]
    public void TdtDeclaresBothCardsAndWhisperTakesItsLicenseFromThePins()
    {
        var tdt = ModelFacts.MultilingualTDT.UnpinnedLicense!;
        Assert.False(tdt.IsPinned);
        Assert.Equal(["NVIDIA upstream model card", "sherpa-onnx conversion model card"], tdt.Declarations.Select(d => d.Source));
        Assert.All(tdt.Declarations, d => Assert.Equal("CC BY 4.0", d.Name));
        Assert.Equal("https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3", tdt.Declarations[0].Url.AbsoluteUri);
        Assert.Equal("Review the upstream and conversion license declarations before downloading.", tdt.Note);
        Assert.Null(ModelFacts.MultilingualWhisper.UnpinnedLicense);
    }

    [Fact]
    public void ConfirmationShowsTheSizeAndThePlacement()
    {
        var (_, manifest) = Pinned(LocalSpeechModel.ParakeetTDT);
        var text = ModelFacts.MultilingualTDT.Confirmation(1_801_783_964, ModelFacts.MultilingualTDT.UnpinnedLicense!, @"T:\x");
        Assert.Equal("Download Parakeet TDT v3 (1.80 GB)?", text.Title);
        Assert.Contains("After that, transcription runs on this PC without the network.", text.Body);
        Assert.Contains("Review the upstream and conversion license declarations before downloading.", text.Body);
        Assert.NotNull(manifest);
    }

    [Fact]
    public void SlotsHaveNoDownloaderWhileThePinsAreAbsent()
    {
        foreach (var facts in new[] { ModelFacts.MultilingualTDT, ModelFacts.MultilingualWhisper })
        {
            var noPins = new ModelSlot(facts, null, Dir("root"));
            Assert.Null(noPins.Downloader);
            Assert.Null(noPins.Manifest);
            Assert.False(noPins.Row.IsReady);
            // A pins file that lacks the model is as good as none.
            var other = new ModelSlot(facts, "{\"models\":{}}"u8.ToArray(), Dir("root"));
            Assert.Null(other.Downloader);
            Assert.False(other.Row.IsReady);
        }
    }
}
