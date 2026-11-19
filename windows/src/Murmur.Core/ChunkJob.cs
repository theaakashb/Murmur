// Port of ChunkJob in app/ModelHelper.swift.
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Murmur.Core;

/// <summary>One recording's ordered helper requests. Audio stays in memory while it is queued.</summary>
public sealed class ChunkJob
{
    sealed record Clip(float[] Samples, bool IsBoundary, bool SentenceEnd);
    readonly Channel<Clip> queue = Channel.CreateUnbounded<Clip>(new() { SingleReader = true });
    public Task<string?> Result { get; }

    public ChunkJob(Func<float[], bool, CancellationToken, Task<string?>> transcribe, CancellationToken ct)
    {
        Result = Task.Run(async () =>
        {
            var texts = new List<string>();
            var sentenceEnds = new List<bool>();
            var repairs = new List<(int Index, JsonArray Values)>();
            try
            {
                await foreach (var clip in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    var answer = ModelHelper.Answer(await transcribe(clip.Samples, clip.IsBoundary, ct).ConfigureAwait(false));
                    if (clip.IsBoundary)
                    {
                        if (answer?["replacements"] is JsonArray found) repairs.Add((texts.Count - 1, found));
                    }
                    else
                    {
                        if (ModelHelper.StringField(answer, "text") is not { } text) return null;
                        texts.Add(text);
                        sentenceEnds.Add(clip.SentenceEnd);
                    }
                }
                foreach (var (index, replacements) in repairs)
                {
                    if (index < 0 || index + 1 >= texts.Count) continue;
                    foreach (var item in replacements.OfType<JsonObject>())
                    {
                        if (ModelHelper.StringField(item, "from") is not { } from || ModelHelper.StringField(item, "to") is not { } to) continue;
                        if (Chunker.Repair(texts[index], texts[index + 1], from, to) is { } fixedText)
                            (texts[index], texts[index + 1]) = fixedText;
                    }
                }
                return Chunker.Join(texts, sentenceEnds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            finally
            {
                queue.Writer.TryComplete();
                while (queue.Reader.TryRead(out _)) { }
            }
        });
    }

    public bool Pending => !Result.IsCompleted;
    public void Enqueue(Chunker.Chunk chunk) => queue.Writer.TryWrite(new Clip(chunk.Samples, false, chunk.SentenceEnd));
    public void EnqueueBoundary(float[] samples) => queue.Writer.TryWrite(new Clip(samples, true, false));
    public void Finish() => queue.Writer.TryComplete();
}
