// Port of app/AudioFormat.swift.
namespace Murmur.Core;

/// <summary>The speech pipeline's shared audio constants (shared/providers.json).</summary>
public static class AudioFormat
{
    /// <summary>The speech models' and uploads' sample rate (16 kHz mono).</summary>
    public static readonly int SampleRate = ProvidersData.Value.AudioFormat.SampleRate;
    /// <summary>Linear peak amplitude (0...1) below which a clip counts as silence: about -40 dBFS.</summary>
    public static readonly float SilencePeak = ProvidersData.Value.AudioFormat.SilencePeak;
    /// <summary>Recordings shorter than this are treated as accidental taps.</summary>
    public static readonly double MinimumSpeechSeconds = ProvidersData.Value.AudioFormat.MinimumSpeechSeconds;

    /// <summary>Pre-check for the caller: true when the recording should not be sent at all. Sending
    /// silence or noise to a transcriber risks a hallucinated transcript. <paramref name="threshold"/> null is <see cref="SilencePeak"/>.</summary>
    public static bool ShouldSkipForSilence(float peakLevel, double? durationSeconds = null, float? threshold = null)
    {
        if (durationSeconds is double duration && duration < MinimumSpeechSeconds) return true;
        if (!float.IsFinite(peakLevel)) return true;
        return Math.Abs(peakLevel) < (threshold ?? SilencePeak);
    }
}
