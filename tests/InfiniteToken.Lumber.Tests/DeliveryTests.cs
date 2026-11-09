using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace InfiniteToken.Lumber.Tests;

public class DeliveryTests
{
    [Fact]
    public void RetriesAFailureUntilItsSent()
    {
        using var test = new TestClient();
        test.Api.Respond = (_, count, _) => Task.FromResult(FakeLumber.Answer(count < 3 ? 503 : 201));
        test.Client.Info("Eventually");
        test.Flush();
        Assert.Equal(3, test.Api.Requests.Count);
        // The same encrypted body each time: a retry never re-encrypts under a new nonce
        Assert.Single(test.Api.Requests.Select(request => Convert.ToBase64String(request.Body)).Distinct());
        Assert.Equal(new[] { "send failed: HTTP 503", "send failed: HTTP 503" }, test.Warnings);
    }

    [Fact]
    public void GivesUpAfterFiveAttempts()
    {
        using var test = new TestClient();
        test.Api.Respond = (_, _, _) => Task.FromResult(FakeLumber.Answer(500));
        test.Client.Info("Never");
        test.Client.Info("Next");
        test.Flush();
        Assert.Equal(10, test.Api.Requests.Count);
        Assert.Equal(2, test.Warnings.Count(warning => warning == "dropping log after 5 failed attempts"));
    }

    [Fact]
    public void WaitsLongerAfterEachFailure()
    {
        using var test = new TestClient();
        var waits = new System.Collections.Concurrent.ConcurrentQueue<int>();
        test.Client.Sender.RetryDelay = failed =>
        {
            waits.Enqueue(failed);
            return TimeSpan.FromMilliseconds(1);
        };
        test.Api.Respond = (_, _, _) => Task.FromResult(FakeLumber.Answer(502));
        test.Client.Info("Never");
        test.Flush();
        Assert.Equal(new[] { 1, 2, 3, 4 }, waits);
    }

    [Fact]
    public void ANetworkErrorIsRetried()
    {
        using var test = new TestClient();
        test.Api.Respond = (_, count, _) => count == 1
            ? throw new HttpRequestException("Connection refused")
            : Task.FromResult(FakeLumber.Answer(201));
        test.Client.Info("After a hiccup");
        test.Flush();
        Assert.Equal(2, test.Api.Requests.Count);
        Assert.Equal(new[] { "send error: Connection refused" }, test.Warnings);
    }

    [Fact]
    public void NoAnswerInTimeIsAFailure()
    {
        using var test = new TestClient(options => options.Timeout = TimeSpan.FromMilliseconds(100));
        test.Api.Respond = async (_, count, cancel) =>
        {
            if (count == 1) await Task.Delay(TimeSpan.FromSeconds(30), cancel);
            return FakeLumber.Answer(201);
        };
        var started = Stopwatch.StartNew();
        test.Client.Info("Slow");
        test.Flush();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(2, test.Api.Requests.Count);
        Assert.Contains("send error: no answer in 0.1 seconds", test.Warnings);
    }

    [Fact]
    public void ARefusedLogIsDroppedAtOnce()
    {
        using var test = new TestClient();
        test.Api.Respond = (_, count, _) => Task.FromResult(count == 1 ? FakeLumber.Answer(401, "Invalid API key") : FakeLumber.Answer(413));
        test.Client.Info("Refused");
        test.Client.Info("Too big");
        test.Flush();
        Assert.Equal(2, test.Api.Requests.Count);
        Assert.Equal(new[] { "log refused: HTTP 401 (Invalid API key)", "log refused: HTTP 413" }, test.Warnings);
    }

    [Fact]
    public void TheMonthlyCapPausesUntilRetryAfter()
    {
        using var test = new TestClient();
        var release = new TaskCompletionSource<bool>();
        test.Api.Respond = async (_, count, _) =>
        {
            if (count == 1)
            {
                await release.Task;
                return FakeLumber.Answer(429, "This team has sent its 10,000 logs for the month on Hobby plan.", "2");
            }
            return FakeLumber.Answer(201);
        };
        test.Client.Info("Over the cap");
        test.Client.Info("Queued behind it");
        release.SetResult(true);
        test.Flush();
        test.Client.Info("While paused");
        test.Flush();
        Assert.Single(test.Api.Requests);
        var warning = Assert.Single(test.Warnings);
        Assert.StartsWith("This team has sent its 10,000 logs for the month on Hobby plan. Dropping logs until ", warning);

        // Retry-After was two seconds
        Thread.Sleep(2100);
        test.Client.Info("After the pause");
        test.Flush();
        Assert.Equal(2, test.Api.Requests.Count);
        Assert.Equal("After the pause", test.Api.Requests[1].Payload.GetProperty("message").GetString());
    }

    [Fact]
    public void A429WithoutRetryAfterPausesAnHour()
    {
        using var test = new TestClient();
        test.Api.Respond = (_, _, _) => Task.FromResult(FakeLumber.Answer(429));
        var before = DateTime.UtcNow;
        test.Client.Info("Capped");
        test.Flush();
        var warning = Assert.Single(test.Warnings);
        Assert.StartsWith("Lumber is refusing logs for now (HTTP 429). Dropping logs until ", warning);
        var until = DateTime.Parse(warning.Substring(warning.IndexOf("until ", StringComparison.Ordinal) + 6).TrimEnd('.'), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(until - before, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
    }

    [Fact]
    public void LogsGoInOrder()
    {
        using var test = new TestClient();
        for (var i = 0; i < 200; i++) test.Client.Info("Log " + i);
        test.Flush();
        Assert.Equal(Enumerable.Range(0, 200).Select(i => "Log " + i), test.Api.Requests.Select(request => request.Payload.GetProperty("message").GetString()));
    }

    [Fact]
    public void LoggingNeverWaitsOnTheNetwork()
    {
        using var test = new TestClient();
        var release = new TaskCompletionSource<bool>();
        test.Api.Respond = async (_, _, _) =>
        {
            await release.Task;
            return FakeLumber.Answer(201);
        };
        var started = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) test.Client.Info("Fast");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), "logging took " + started.Elapsed);
        Assert.False(test.Client.Flush(TimeSpan.FromMilliseconds(50)));
        release.SetResult(true);
        test.Flush();
        Assert.Equal(1000, test.Api.Requests.Count);
    }

    [Fact]
    public void AFullQueueDropsNewLogsWithOneWarning()
    {
        using var test = new TestClient(options => options.MaxQueue = 3);
        var release = new TaskCompletionSource<bool>();
        test.Api.Respond = async (_, _, _) =>
        {
            await release.Task;
            return FakeLumber.Answer(201);
        };
        test.Client.Info("In flight");
        SpinWait.SpinUntil(() => test.Api.Requests.Count == 1, TimeSpan.FromSeconds(5));
        for (var i = 0; i < 10; i++) test.Client.Info("Queued " + i);
        release.SetResult(true);
        test.Flush();
        Assert.Equal(new[] { "In flight", "Queued 0", "Queued 1", "Queued 2" }, test.Api.Requests.Select(request => request.Payload.GetProperty("message").GetString()));
        Assert.Equal(new[] { "dropping logs: 3 are already waiting to send" }, test.Warnings);
    }

    [Fact]
    public async Task FlushAsyncWaitsForTheQueue()
    {
        using var test = new TestClient();
        test.Api.Respond = async (_, _, _) =>
        {
            await Task.Delay(20);
            return FakeLumber.Answer(201);
        };
        Assert.True(await test.Client.FlushAsync(TimeSpan.FromSeconds(1)));
        for (var i = 0; i < 5; i++) test.Client.Info("Log " + i);
        Assert.True(await test.Client.FlushAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(5, test.Api.Requests.Count);
    }

    [Fact]
    public async Task FlushAsyncGivesUpAtItsLimit()
    {
        using var test = new TestClient();
        var release = new TaskCompletionSource<bool>();
        test.Api.Respond = async (_, _, _) =>
        {
            await release.Task;
            return FakeLumber.Answer(201);
        };
        test.Client.Info("Stuck");
        Assert.False(await test.Client.FlushAsync(TimeSpan.FromMilliseconds(50)));
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Client.FlushAsync(null, cancel.Token));
        release.SetResult(true);
        Assert.True(await test.Client.FlushAsync());
    }

    [Fact]
    public void DisposeStopsAtTheShutdownTimeout()
    {
        var test = new TestClient(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200));
        test.Api.Respond = async (_, _, cancel) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancel);
            return FakeLumber.Answer(201);
        };
        test.Client.Info("Never answered");
        var started = Stopwatch.StartNew();
        test.Dispose();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), "disposing took " + started.Elapsed);
        Assert.Contains("closed with logs still waiting to send", test.Warnings);
    }

    [Fact]
    public void DisposeSendsWhatsQueued()
    {
        var test = new TestClient();
        for (var i = 0; i < 20; i++) test.Client.Info("Log " + i);
        test.Dispose();
        Assert.Equal(20, test.Api.Requests.Count);
        Assert.Empty(test.Warnings);
    }

    [Fact]
    public void DisposeStopsRetrying()
    {
        var test = new TestClient(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(100));
        test.Client.Sender.RetryDelay = _ => TimeSpan.FromMinutes(1);
        test.Api.Respond = (_, _, _) => Task.FromResult(FakeLumber.Answer(503));
        test.Client.Info("Failing");
        var started = Stopwatch.StartNew();
        test.Dispose();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), "disposing took " + started.Elapsed);
        Assert.Single(test.Api.Requests);
    }

#if NET
    [Fact]
    public async Task DisposeAsyncSendsWhatsQueued()
    {
        var test = new TestClient();
        for (var i = 0; i < 20; i++) test.Client.Info("Log " + i);
        await test.Client.DisposeAsync();
        Assert.Equal(20, test.Api.Requests.Count);
    }
#endif

    [Fact]
    public void ManyThreadsAtOnce()
    {
        using var test = new TestClient();
        Parallel.For(0, 8, thread =>
        {
            for (var i = 0; i < 100; i++) test.Client.Info("Thread " + thread, new { i });
        });
        test.Flush();
        Assert.Equal(800, test.Api.Requests.Count);
        Assert.All(Enumerable.Range(0, 8), thread =>
            Assert.Equal(
                Enumerable.Range(0, 100),
                test.Api.Requests.Select(request => request.Payload)
                    .Where(payload => payload.GetProperty("message").GetString() == "Thread " + thread)
                    .Select(payload => payload.GetProperty("metadata").GetProperty("i").GetInt32())));
    }
}
