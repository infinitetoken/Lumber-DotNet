using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InfiniteToken.Lumber;

/// <summary>
/// Sends logs to POST /logs from one background loop, in the order they were logged, over HttpClient's kept-alive
/// connections. A log is encrypted here, under its own random nonce, just before its first try. What each answer means
/// and how long to wait are Lumber-Kit's (see <see cref="Contract"/>). Enqueue never blocks or throws.
/// </summary>
internal sealed class Sender
{
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Uri url;
    private readonly AuthenticationHeaderValue authorization;
    private readonly string userAgent;
    private readonly TimeSpan timeout;
    private readonly LogCipher cipher;
    private readonly Action<string> warn;
    private readonly int maxQueue;

    private readonly ConcurrentQueue<QueuedLog> queue = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly Task loop;
    private List<TaskCompletionSource<bool>> idleWaiters = new();
    private int queued;
    private int pending;
    private long pausedUntilTicks;
    private int fullWarned;
    private int closed;

    public Sender(HttpClient http, bool ownsHttp, Uri url, string token, string userAgent, TimeSpan timeout, int maxQueue, LogCipher cipher, Action<string> warn)
    {
        this.http = http;
        this.ownsHttp = ownsHttp;
        this.url = url;
        authorization = new AuthenticationHeaderValue("Bearer", token);
        this.userAgent = userAgent;
        this.timeout = timeout;
        this.maxQueue = maxQueue;
        this.cipher = cipher;
        this.warn = warn;
        loop = Task.Run(RunAsync);
    }

    /// <summary>How long to wait after a log's nth failure. The tests shorten it.</summary>
    internal Func<int, TimeSpan> RetryDelay { get; set; } = Contract.RetryDelay;

    private bool Paused => DateTime.UtcNow.Ticks < Interlocked.Read(ref pausedUntilTicks);

    /// <summary>Queues a log's plaintext. A log that doesn't fit is dropped, with one warning until there's room again.</summary>
    public void Enqueue(int level, byte[] plaintext)
    {
        if (Volatile.Read(ref closed) != 0 || Paused) return;
        if (Interlocked.Increment(ref queued) > maxQueue)
        {
            Interlocked.Decrement(ref queued);
            if (Interlocked.Exchange(ref fullWarned, 1) == 0)
            {
                warn(string.Format(CultureInfo.InvariantCulture, "dropping logs: {0} are already waiting to send", maxQueue));
            }
            return;
        }
        lock (gate) pending++;
        queue.Enqueue(new QueuedLog(level, plaintext));
        signal.Release();
        // Closed while this log was going in: the loop has stopped, so nothing would ever take it
        if (Volatile.Read(ref closed) != 0) DropQueued();
    }

    /// <summary>Waits until every queued log is sent or dropped: false when the timeout passed first.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var deadline = Deadline(timeout);
        lock (gate)
        {
            while (pending > 0)
            {
                var left = Left(deadline);
                if (left == TimeSpan.Zero) return false;
                Monitor.Wait(gate, left == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Min(int.MaxValue, Math.Ceiling(left.TotalMilliseconds)));
            }
        }
        return true;
    }

    public async Task<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> idle;
        lock (gate)
        {
            if (pending == 0) return true;
            idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            idleWaiters.Add(idle);
        }
        if (timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled) return await idle.Task.ConfigureAwait(false);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = await Task.WhenAny(idle.Task, Task.Delay(timeout, waiting.Token)).ConfigureAwait(false);
        waiting.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        return finished == idle.Task;
    }

    /// <summary>Sends what's queued, waiting up to <paramref name="timeout"/>, and stops. Logs after this are dropped.</summary>
    public bool Close(TimeSpan timeout)
    {
        var deadline = Deadline(timeout);
        var flushed = Flush(timeout);
        Stop();
        try
        {
            var left = Left(deadline);
            loop.Wait(left == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Min(left.Ticks, TimeSpan.TicksPerSecond)));
        }
        catch (AggregateException)
        {
        }
        Release();
        return flushed;
    }

    public async Task<bool> CloseAsync(TimeSpan timeout)
    {
        var flushed = await FlushAsync(timeout, CancellationToken.None).ConfigureAwait(false);
        Stop();
        await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        Release();
        return flushed;
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel();
    }

    private void Release()
    {
        if (ownsHttp) http.Dispose();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await signal.WaitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DropQueued();
                return;
            }
            if (!queue.TryDequeue(out var log)) continue;
            if (Interlocked.Decrement(ref queued) <= maxQueue / 2) Volatile.Write(ref fullWarned, 0);
            try
            {
                await DeliverAsync(log).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                warn("send error: " + exception.Message);
            }
            finally
            {
                Done(1);
            }
        }
    }

    private async Task DeliverAsync(QueuedLog log)
    {
        var body = Body(log);
        for (var failed = 0; ;)
        {
            if (stop.IsCancellationRequested || Paused) return;
            var answer = await PostAsync(body).ConfigureAwait(false);
            switch (Contract.Outcome(answer.Status))
            {
                case DeliveryOutcome.Sent:
                    return;
                case DeliveryOutcome.Pause:
                    Pause(answer);
                    return;
                case DeliveryOutcome.Drop:
                    // A bad key or a malformed log won't go through on a later try
                    warn(answer.Message is { Length: > 0 } message
                        ? string.Format(CultureInfo.InvariantCulture, "log refused: HTTP {0} ({1})", answer.Status, message)
                        : string.Format(CultureInfo.InvariantCulture, "log refused: HTTP {0}", answer.Status));
                    return;
            }
            warn(answer.Status is { } status
                ? string.Format(CultureInfo.InvariantCulture, "send failed: HTTP {0}", status)
                : "send error: " + answer.Error);
            failed++;
            if (failed >= Contract.MaxAttempts)
            {
                warn(string.Format(CultureInfo.InvariantCulture, "dropping log after {0} failed attempts", Contract.MaxAttempts));
                return;
            }
            await Task.Delay(RetryDelay(failed), stop.Token).ConfigureAwait(false);
        }
    }

    // POST /logs' body: the level stays plaintext for filtering and pushes, everything else is encrypted
    private byte[] Body(QueuedLog log)
    {
        var nonce = new byte[Contract.NonceBytes];
        Random.GetBytes(nonce);
        var sealedBytes = cipher.Seal(nonce, log.Plaintext);
        var body = new StringBuilder(64 + sealedBytes.Length * 4 / 3);
        body.Append("{\"level\":").Append(log.Level.ToString(CultureInfo.InvariantCulture))
            .Append(",\"encrypted\":{\"nonce\":\"").Append(Convert.ToBase64String(nonce))
            .Append("\",\"payload\":\"").Append(Convert.ToBase64String(sealedBytes))
            .Append("\",\"keyVersion\":").Append(Contract.KeyVersion.ToString(CultureInfo.InvariantCulture))
            .Append("}}");
        return Encoding.UTF8.GetBytes(body.ToString());
    }

    private async Task<Answer> PostAsync(byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = authorization;
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        attempt.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, attempt.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            string? message = null;
            // .NET Framework leaves Content null on an answer with no body
            if (status >= 300 && response.Content != null)
            {
                message = Json.MessageOf(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            return new Answer(status, RetryAfter(response), message, null);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return new Answer(null, null, null, string.Format(CultureInfo.InvariantCulture, "no answer in {0} seconds", timeout.TotalSeconds));
        }
        catch (HttpRequestException exception)
        {
            return new Answer(null, null, null, exception.InnerException?.Message ?? exception.Message);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    // Stops sending until the API's Retry-After (the start of the next month, for the monthly cap), dropping what's
    // queued, with one warning for the whole pause
    private void Pause(Answer answer)
    {
        var wait = answer.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : Contract.PauseFor;
        var until = DateTime.UtcNow + wait;
        Interlocked.Exchange(ref pausedUntilTicks, until.Ticks);
        DropQueued();
        var message = answer.Message is { Length: > 0 } given ? given : "Lumber is refusing logs for now (HTTP 429).";
        warn(message + " Dropping logs until " + until.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + ".");
    }

    private void DropQueued()
    {
        var dropped = 0;
        while (queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref queued);
            dropped++;
        }
        Done(dropped);
    }

    private void Done(int count)
    {
        if (count == 0) return;
        List<TaskCompletionSource<bool>>? idle = null;
        lock (gate)
        {
            pending -= count;
            if (pending == 0)
            {
                Monitor.PulseAll(gate);
                if (idleWaiters.Count > 0)
                {
                    idle = idleWaiters;
                    idleWaiters = new List<TaskCompletionSource<bool>>();
                }
            }
        }
        if (idle != null)
        {
            foreach (var waiter in idle) waiter.TrySetResult(true);
        }
    }

    private static DateTime? Deadline(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan ? null : DateTime.UtcNow + (timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);

    private static TimeSpan Left(DateTime? deadline)
    {
        if (deadline == null) return Timeout.InfiniteTimeSpan;
        var left = deadline.Value - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private sealed class QueuedLog
    {
        public QueuedLog(int level, byte[] plaintext)
        {
            Level = level;
            Plaintext = plaintext;
        }

        public int Level { get; }

        public byte[] Plaintext { get; }
    }

    private sealed class Answer
    {
        public Answer(int? status, TimeSpan? retryAfter, string? message, string? error)
        {
            Status = status;
            RetryAfter = retryAfter;
            Message = message;
            Error = error;
        }

        public int? Status { get; }

        public TimeSpan? RetryAfter { get; }

        public string? Message { get; }

        public string? Error { get; }
    }
}
