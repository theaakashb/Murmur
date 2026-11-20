using SherpaOnnx;

namespace Murmur.Speech;

/// <summary>
/// Speaker separation for `--diarize KEPT_WAV THRESHOLD`: sherpa-onnx offline diarization (pyannote segmentation-3.0 + a WeSpeaker embedding model + agglomerative clustering).
/// This is not the Mac's community-1 + VBx pipeline, so the threshold is sherpa's cosine-distance clustering threshold, tuned on real meetings (PLAN.md "Speech recognition" 7).
/// </summary>
static class Diarizer
{
    /// <param name="segmentationFolder">holds model.onnx (pyannote segmentation-3.0)</param>
    /// <returns>One object per speaker turn: speaker id, start and end in seconds.</returns>
    public static object[] Run(string segmentationFolder, string embeddingModel, string wav, double threshold)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = Path.Combine(segmentationFolder, "model.onnx");
        config.Segmentation.NumThreads = 2;
        config.Embedding.Model = embeddingModel;
        config.Embedding.NumThreads = 2;
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = (float)threshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;
        using var diarization = new OfflineSpeakerDiarization(config);
        if (diarization.SampleRate != WavReader.Rate) throw new InvalidDataException("Unexpected diarization sample rate");
        var segments = diarization.Process(WavReader.Read(wav));
        return segments.Select(s => (object)new SortedDictionary<string, object?> { ["speaker"] = s.Speaker.ToString(System.Globalization.CultureInfo.InvariantCulture), ["start"] = (double)s.Start, ["end"] = (double)s.End }).ToArray();
    }
}
