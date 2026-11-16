// The speech pipeline's shared audio constants; dependency-free so any file or probe can compile it.
enum AudioFormat {
    /// The speech models' and uploads' sample rate (16 kHz mono).
    static let sampleRate = 16_000
    /// Linear peak amplitude (0...1) below which a clip counts as silence: about -40 dBFS (the pre-check, tail trim and loud-window
    /// logic). [Guessing] Tune it against real recordings; too high drops quiet speech.
    static let silencePeak: Float = 0.01
    /// Recordings shorter than this are treated as accidental taps.
    static let minimumSpeechSeconds: Double = 0.3

    /// Pre-check for the caller: true when the recording should not be sent at all. Sending
    /// silence or noise to a transcriber risks a hallucinated transcript.
    static func shouldSkipForSilence(peakLevel: Float, durationSeconds: Double? = nil, threshold: Float = silencePeak) -> Bool {
        if let duration = durationSeconds, duration < minimumSpeechSeconds { return true }
        guard peakLevel.isFinite else { return true }
        return abs(peakLevel) < threshold
    }
}
