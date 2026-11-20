using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class SpeakerAudioTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "murmur-speaker-" + Guid.NewGuid().ToString("N"));
    static readonly string Id = Guid.NewGuid().ToString("D");

    [Fact]
    public void ChunksLandAtTheirOwnTimeAndGapsReadAsSilence()
    {
        var path = SpeakerAudio.File(root, Id);
        SpeakerAudio.Write(WAVAudio.EncodePCM16(Enumerable.Repeat(0.5f, 16000).ToArray(), 16000), 2, path);
        SpeakerAudio.Write(WAVAudio.EncodePCM16(Enumerable.Repeat(0.25f, 8000).ToArray(), 16000), 0.5, path);
        SpeakerAudio.Finish(path);
        var audio = WAVAudio.Decode(File.ReadAllBytes(path))!;
        Assert.Equal(16000, audio.SampleRate);
        Assert.Equal(48000, audio.Samples.Length);
        Assert.Equal(0, audio.Samples[0]);                       // before the first chunk
        Assert.InRange(audio.Samples[8000 + 1], 0.24f, 0.26f);   // second chunk, written first at 0.5 s
        Assert.Equal(0, audio.Samples[16000 + 100]);             // the gap from 1 s to 2 s
        Assert.InRange(audio.Samples[32000 + 5], 0.49f, 0.51f);
    }

    [Fact]
    public void AWrongRateOrANameThatIsNotAMeetingIdIsRefused()
    {
        Assert.Throws<RetentionError>(() => SpeakerAudio.Write(WAVAudio.EncodePCM16(new float[100], 8000), 0, SpeakerAudio.File(root, Id)));
        Assert.Throws<RetentionError>(() => SpeakerAudio.File(root, "../x"));
    }

    [Fact]
    public void PurgeKeepsOnlyPendingMeetingsFilesAndNeverTheActiveOnes()
    {
        var pending = Guid.NewGuid().ToString("D"); var stale = Guid.NewGuid().ToString("D"); var active = Guid.NewGuid().ToString("D");
        foreach (var id in new[] { pending, stale, active }) SpeakerAudio.Write(WAVAudio.EncodePCM16(new float[1600], 16000), 0, SpeakerAudio.File(root, id));
        File.WriteAllText(Path.Combine(root, "note.txt"), "x");
        Assert.Equal(0, SpeakerAudio.Purge(root, new HashSet<string> { pending }, new HashSet<string> { active }));
        Assert.True(File.Exists(SpeakerAudio.File(root, pending)));
        Assert.False(File.Exists(SpeakerAudio.File(root, stale)));
        Assert.True(File.Exists(SpeakerAudio.File(root, active)));
        Assert.False(File.Exists(Path.Combine(root, "note.txt")));
        SpeakerAudio.Purge(root, new HashSet<string> { pending }, now: DateTime.UtcNow.AddSeconds(RetryWindow.LifetimeSeconds + 1));
        Assert.False(File.Exists(SpeakerAudio.File(root, pending))); // past the retry window
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
}
