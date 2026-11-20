// Port of SpeakerAudio and SpeakerFacts in app/MeetingSpeakers.swift: the call's audio kept for diarization.
// One 16 kHz mono 16-bit WAV per meeting, each chunk at its own time so gaps read as silence. Deleted when separation finishes or is skipped,
// with its meeting, and in any case RetryWindow.Text after it was created. The Me stream is never kept (except microphone-only recordings with the room switch).
using System.Globalization;

namespace Murmur.Core;

public static class SpeakerAudio
{
    public const int Header = 44;
    static readonly int Rate = AudioFormat.SampleRate;

    public static string File(string root, string id) =>
        Guid.TryParseExact(id, "D", out _) ? Path.Combine(root, id + ".wav") : throw new RetentionError("Invalid meeting identifier");

    /// <summary>Writes a chunk's PCM16 data (the bytes of a <see cref="WAVAudio.EncodePCM16"/> file) at <paramref name="start"/> seconds.</summary>
    public static void Write(byte[] chunkWav, double start, string path)
    {
        if (WAVAudio.Decode(chunkWav) is not { SampleRate: var rate } || rate != Rate || chunkWav.Length <= Header) throw new RetentionError("Could not keep call audio");
        PrivateFiles.PrepareDirectory(Path.GetDirectoryName(path)!);
        if (FileLinks.IsLink(path)) throw new RetentionError("Could not keep call audio");
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (file.Length == 0) file.Write(WavHeader(0));
        file.Position = Header + Math.Max(0, (long)Math.Round(start * Rate, MidpointRounding.AwayFromZero)) * 2;
        file.Write(chunkWav, Header, chunkWav.Length - Header);
    }

    /// <summary>Sets the header's sizes from the file's length; the helper reads it after this.</summary>
    public static void Finish(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        file.Position = 0;
        file.Write(WavHeader((int)Math.Max(0, file.Length - Header)));
    }

    public static byte[] WavHeader(int dataBytes)
    {
        var h = new byte[Header];
        void Put(int at, byte[] b) => b.CopyTo(h, at);
        Put(0, "RIFF"u8.ToArray()); Put(4, BitConverter.GetBytes(36 + dataBytes)); Put(8, "WAVEfmt "u8.ToArray());
        Put(16, BitConverter.GetBytes(16)); Put(20, BitConverter.GetBytes((short)1)); Put(22, BitConverter.GetBytes((short)1));
        Put(24, BitConverter.GetBytes(Rate)); Put(28, BitConverter.GetBytes(Rate * 2)); Put(32, BitConverter.GetBytes((short)2)); Put(34, BitConverter.GetBytes((short)16));
        Put(36, "data"u8.ToArray()); Put(40, BitConverter.GetBytes(dataBytes));
        return h;
    }

    /// <summary>Deletes kept audio older than <see cref="RetryWindow.LifetimeSeconds"/> (or dated in the future), files of meetings not in <paramref name="keep"/>
    /// (those still waiting for diarization), and anything that is not a kept file. <paramref name="active"/> is never touched. Returns how many could not be deleted.</summary>
    public static int Purge(string root, IReadOnlySet<string> keep, IReadOnlySet<string>? active = null, DateTime? now = null)
    {
        if (FileLinks.IsLink(root) || !Directory.Exists(root)) return 0;
        int failed = 0;
        var current = SwiftDate.Normalize(now ?? DateTime.UtcNow);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (active?.Contains(id) == true) continue;
            var created = System.IO.File.GetCreationTimeUtc(path);
            if (Path.GetExtension(path) != ".wav" || !keep.Contains(id) || FileLinks.IsLink(path) || !System.IO.File.Exists(path) ||
                (current - created).TotalSeconds >= RetryWindow.LifetimeSeconds || created > current)
                try { FileLinks.DeleteTree(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        return failed;
    }
}

public static class SpeakerFacts
{
    public const string SettingKey = "meetingSpeakers", RoomKey = "meetingRoomSpeakers", DisplayName = "Separate speakers after a meeting";

    public static string Explanation(bool room) =>
        $"After Stop, the call’s audio is split into Speaker 1, Speaker 2… on this PC. To do that, Murmur keeps the call’s audio ({(room ? "or your microphone for microphone-only recordings" : "never your microphone")}) until separation finishes, at most {RetryWindow.Text}, then deletes it. Voice prints are discarded after each meeting, and nobody is recognised across meetings.";

    public static string Credits =>
        "Speaker separation uses the pyannote segmentation-3.0 model (MIT, CNRS) and a WeSpeaker VoxCeleb ResNet34 embedding model (Apache-2.0), run with sherpa-onnx.";

    public static string? Status(SpeakerSeparation? separation, bool running, bool room = false) => separation?.State switch
    {
        SpeakerState.Pending => running ? "Separating speakers…" : $"Waiting to separate speakers. {(room ? "Microphone audio" : "The call’s audio")} is kept for up to {RetryWindow.Text}.",
        SpeakerState.Failed => $"Speakers could not be separated, so {(room ? "the microphone is labelled Room" : "the call’s side is labelled Them")}. Its audio was deleted.",
        _ => null,
    };
}
