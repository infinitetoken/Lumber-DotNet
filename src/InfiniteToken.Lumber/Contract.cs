using System;
using System.Collections.Generic;
using System.Globalization;

namespace InfiniteToken.Lumber;

/// <summary>What a POST /logs answer means for a log.</summary>
internal enum DeliveryOutcome
{
    Sent,
    Retry,
    Drop,
    Pause,
}

/// <summary>
/// Lumber-Kit's contract in C#: the constants, API key format, payload and delivery rules every Lumber plugin shares.
/// tests/contract holds the kit's schema and known answers, which everything here is tested against.
/// </summary>
internal static class Contract
{
    public const string DefaultEndpoint = "https://api.lumber.infinitetoken.com";
    public const string IngestPath = "/logs";
    public const int KeyVersion = 1;
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    public const int MaxAttempts = 5;
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PauseFor = TimeSpan.FromHours(1);

    /// <summary>An API key's halves, <c>&lt;token&gt;.&lt;encryptionKey&gt;</c>. A value with no dot is all token.</summary>
    public static void SplitApiKey(string? apiKey, out string token, out string? encryptionKey)
    {
        var value = (apiKey ?? "").Trim();
        var dot = value.IndexOf('.');
        token = dot < 0 ? value : value.Substring(0, dot);
        encryptionKey = dot < 0 ? null : value.Substring(dot + 1);
    }

    /// <summary>
    /// The plaintext a log encrypts, from normalized values (<see cref="Json.Normalize(object)"/>). requestId and traceId become
    /// the payload's own fields, the log's context goes over the automatic context, and every other key goes into
    /// metadata, where an explicit metadata key wins. A context or metadata that isn't an object is kept as an ordinary
    /// metadata key, and a null one is left out.
    /// </summary>
    public static Dictionary<string, object?> BuildPayload(
        int level,
        string message,
        IDictionary<string, object?>? data,
        IDictionary<string, object?>? autoContext)
    {
        var rest = new Dictionary<string, object?>(StringComparer.Ordinal);
        object? requestId = null, traceId = null, givenContext = null, givenMetadata = null;
        if (data != null)
        {
            foreach (var pair in data)
            {
                switch (pair.Key)
                {
                    case "requestId": requestId = pair.Value; break;
                    case "traceId": traceId = pair.Value; break;
                    case "context": givenContext = pair.Value; break;
                    case "metadata": givenMetadata = pair.Value; break;
                    default: rest[pair.Key] = pair.Value; break;
                }
            }
        }

        var context = givenContext as Dictionary<string, object?>;
        var metadata = givenMetadata as Dictionary<string, object?>;
        if (givenContext != null && context == null) rest["context"] = givenContext;
        if (givenMetadata != null && metadata == null) rest["metadata"] = givenMetadata;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["level"] = level, ["message"] = message };
        if (requestId != null) payload["requestId"] = Identifier(requestId);
        if (traceId != null) payload["traceId"] = Identifier(traceId);
        if (autoContext != null && autoContext.Count > 0) payload["context"] = Merge(autoContext, context);
        else if (context != null) payload["context"] = Merge(context, null);
        if (rest.Count > 0) payload["metadata"] = Merge(rest, metadata);
        else if (metadata != null) payload["metadata"] = Merge(metadata, null);
        return payload;
    }

    /// <summary>What to do with a log once POST /logs answers with <paramref name="status"/>, or null for no answer.</summary>
    public static DeliveryOutcome Outcome(int? status)
    {
        if (status == null) return DeliveryOutcome.Retry;
        if (status >= 200 && status < 300) return DeliveryOutcome.Sent;
        if (status == 429) return DeliveryOutcome.Pause;
        if (status >= 400 && status < 500) return DeliveryOutcome.Drop;
        return DeliveryOutcome.Retry;
    }

    /// <summary>How long to wait before trying a log again after its nth failure: 1s, 2s, 4s and so on, up to 30s.</summary>
    public static TimeSpan RetryDelay(int failedAttempts)
    {
        var doublings = Math.Max(0, failedAttempts - 1);
        if (doublings >= 30) return MaxDelay;
        var delay = TimeSpan.FromTicks(BaseDelay.Ticks << doublings);
        return delay > MaxDelay ? MaxDelay : delay;
    }

    // The schema's requestId and traceId are strings: a number or a flag goes as its text
    private static object Identifier(object value) => value switch
    {
        string or Dictionary<string, object?> or List<object?> => value,
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static Dictionary<string, object?> Merge(IDictionary<string, object?> under, IDictionary<string, object?>? over)
    {
        var merged = new Dictionary<string, object?>(under, StringComparer.Ordinal);
        if (over != null)
        {
            foreach (var pair in over) merged[pair.Key] = pair.Value;
        }
        return merged;
    }
}
