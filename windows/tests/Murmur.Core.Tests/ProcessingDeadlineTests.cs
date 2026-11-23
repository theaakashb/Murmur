using Murmur.Core;

namespace Murmur.Core.Tests;

public class ProcessingDeadlineTests
{
    [Fact]
    public void TranscriptionDeadlineGrowsWithTheClip()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), ProcessingDeadline.Transcription(0));
        Assert.Equal(TimeSpan.FromSeconds(120 + 2 * 81.8), ProcessingDeadline.Transcription(81.8));
        Assert.Equal(TimeSpan.FromSeconds(720), ProcessingDeadline.Transcription(300));
        Assert.Equal(TimeSpan.FromSeconds(120), ProcessingDeadline.Transcription(-5));
    }

    [Fact]
    public async Task WaitOrAnswersTheFallbackWhenTheTaskNeverFinishes()
    {
        var never = new TaskCompletionSource<string?>().Task;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await ProcessingDeadline.WaitOr(never, TimeSpan.FromMilliseconds(200), null));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal("focus", await ProcessingDeadline.WaitOr(Task.FromResult<string?>("focus"), TimeSpan.FromSeconds(5), null));
    }

    [Fact]
    public async Task RunCancelsTheWorkAtTheLimit()
    {
        var (result, timedOut) = await ProcessingDeadline.RunAsync(async ct => { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { } return "cancelled"; }, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Equal("cancelled", result);
        Assert.True(timedOut);
    }

    [Fact]
    public async Task RunReportsTheCallersCancelAsNotTimedOut()
    {
        using var user = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var (result, timedOut) = await ProcessingDeadline.RunAsync(async ct => { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { } return "cancelled"; }, TimeSpan.FromSeconds(30), user.Token);
        Assert.Equal("cancelled", result);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task RunLeavesAFinishedAnswerAlone()
    {
        var (result, timedOut) = await ProcessingDeadline.RunAsync(ct => Task.FromResult(7), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(7, result);
        Assert.False(timedOut);
    }

    // A helper that ignores its token (a pipe write that cannot be interrupted): Run stops waiting five seconds past the limit.
    [Fact]
    public async Task RunGivesUpOnWorkThatIgnoresItsToken()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (result, timedOut) = await ProcessingDeadline.RunAsync(ct => new TaskCompletionSource<string>().Task, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Null(result);
        Assert.True(timedOut);
        Assert.InRange(clock.Elapsed.TotalSeconds, 4.5, 15);
    }
}
