// The speech pipeline's shared audio constants; dependency-free so any file or probe can compile it.
enum AudioFormat {
    /// The speech models' and uploads' sample rate (16 kHz mono).
    static let sampleRate = 16_000
    /// Linear peak amplitude (0...1) below which a clip counts as silence: about -40 dBFS (the pre-check, tail trim and loud-window
    /// logic). [Guessing] Tune it against real recordings; too high drops quiet speech.
    static let silencePeak: Float = 0.01
}
