// Ports the ordered ChunkJob checks from app/check_controller.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class ChunkerTests
{
    [Fact]
    public void EmitsContiguousChunksAndWholeBlockBoundary()
    {
        var parts = new List<Chunker.Chunk>();
        var boundaries = new List<float[]>();
        var chunker = new Chunker(parts.Add, boundaries.Add);
        var rate = AudioFormat.SampleRate;
        // 0.4 s blocks make the 3 s overlap round up to 3.2 s.
        var blocks = Enumerable.Range(0, 51).Select(i => Enumerable.Repeat(i is 31 or 32 ? 0f : i + 1f, (int)(rate * 0.4)).ToArray()).ToArray();
        foreach (var block in blocks.Take(49)) chunker.Append(block);
        Assert.Empty(parts);

        chunker.Append(blocks[49]);
        var first = Assert.Single(parts);
        Assert.True(first.SentenceEnd);
        Assert.InRange(first.Samples.Length, 12 * rate, 14 * rate);
        var overlap = (int)(3.2 * rate);
        Assert.Equal(blocks.SelectMany(b => b).Skip(first.Samples.Length - overlap).Take(2 * overlap), Assert.Single(boundaries));

        chunker.Append(blocks[50]);
        chunker.Finish();
        Assert.Equal(2, parts.Count);
        Assert.Equal(blocks.SelectMany(b => b), parts.SelectMany(p => p.Samples));
        chunker.Finish();
        Assert.Equal(2, parts.Count);
    }

    [Fact]
    public async Task FirstFailureDropsQueuedChunks()
    {
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<int>();
        var job = new ChunkJob((samples, _, _) => { sent.Add((int)samples[0]); return first.Task; }, CancellationToken.None);
        job.Enqueue(new([1], false));
        job.Enqueue(new([2], false));
        job.EnqueueBoundary([3]);
        job.Enqueue(new([4], false));
        job.Finish();
        first.SetResult(null);
        Assert.Null(await job.Result);
        Assert.Equal(new[] { 1 }, sent);
    }

    [Theory]
    [InlineData("""{"replacements":[{"from":"open router","to":"OpenRouter"}]}""", "Route it through OpenRouter, then deploy.")]
    [InlineData(null, "Route it through open. Router, then deploy.")]
    public async Task BoundaryIsOrderedAndOptional(string? boundary, string expected)
    {
        var sent = new List<(int, bool)>();
        var job = new ChunkJob((samples, clip, _) =>
        {
            var id = (int)samples[0];
            sent.Add((id, clip));
            return Task.FromResult(id switch { 1 => """{"text":"Route it through open."}""", 2 => boundary, _ => """{"text":"Router, then deploy."}""" });
        }, CancellationToken.None);
        job.Enqueue(new([1], false));
        job.EnqueueBoundary([2]);
        job.Enqueue(new([3], false));
        Assert.False(job.Result.IsCompleted);
        job.Finish();
        Assert.Equal(expected, await job.Result);
        Assert.Equal(new[] { (1, false), (2, true), (3, false) }, sent);
    }

    [Fact]
    public async Task CancellationDropsLaterChunksUnread()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var job = new ChunkJob((_, _, _) => throw new InvalidOperationException("Canceled job sent audio"), cancellation.Token);
        job.Enqueue(new([1], false));
        job.Finish();
        Assert.Null(await job.Result);
    }

}
