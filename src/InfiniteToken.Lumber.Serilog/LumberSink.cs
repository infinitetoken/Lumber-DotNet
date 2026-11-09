using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace InfiniteToken.Lumber.Serilog;

/// <summary>
/// A Serilog sink that sends events to Lumber. Add it with <c>.WriteTo.Lumber(apiKey)</c>. An event's properties go into
/// the log's metadata, except <c>RequestId</c> and <c>TraceId</c> (or <c>requestId</c> and <c>traceId</c>), the log's own
/// fields, and <c>SourceContext</c>, its context's <c>logger</c>.
/// </summary>
public sealed class LumberSink : ILogEventSink, IDisposable
#if NET
    , IAsyncDisposable
#endif
{
    private readonly LumberClient? client;
    private readonly bool ownsClient;
    private readonly MessageTemplateTextFormatter formatter;

    /// <summary>A sink with its own client, made from <paramref name="options"/>, which it disposes with itself.</summary>
    /// <param name="options">The client's options. With no API key, the sink warns once and sends nothing.</param>
    /// <param name="formatProvider">Formats values in the message: the current culture unless given.</param>
    public LumberSink(LumberOptions options, IFormatProvider? formatProvider = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        formatter = Formatter(formatProvider);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            const string warning = "No API key, so logs aren't sent to Lumber. Set LUMBER_API_KEY, or pass apiKey to WriteTo.Lumber.";
            try
            {
                if (options.OnWarning != null) options.OnWarning(warning);
                else Console.Error.WriteLine("[lumber] " + warning);
            }
            catch (Exception)
            {
            }
            return;
        }
        client = new LumberClient(options);
        ownsClient = true;
    }

    /// <summary>A sink that logs with a client you made and dispose yourself.</summary>
    /// <param name="client">The client to log with.</param>
    /// <param name="formatProvider">Formats values in the message: the current culture unless given.</param>
    public LumberSink(LumberClient client, IFormatProvider? formatProvider = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        formatter = Formatter(formatProvider);
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        if (client == null || logEvent == null) return;
        var message = new StringWriter();
        formatter.Format(logEvent, message);

        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        Dictionary<string, object?>? context = null;
        foreach (var property in logEvent.Properties)
        {
            if (property.Key == "SourceContext" && property.Value is ScalarValue { Value: string logger })
            {
                context = new Dictionary<string, object?> { ["logger"] = logger };
                continue;
            }
            data[property.Key] = Simplify(property.Value);
        }
        // The trace Serilog took from Activity.Current, unless the event names its own
        if (logEvent.TraceId is { } traceId && !data.ContainsKey("traceId") && !data.ContainsKey("TraceId"))
        {
            data["traceId"] = traceId.ToHexString();
            if (logEvent.SpanId is { } spanId && !data.ContainsKey("spanId")) data["spanId"] = spanId.ToHexString();
        }

        client.Write(Level(logEvent.Level), message.ToString(), logEvent.Exception, data, context);
    }

    /// <summary>Sends what's queued, if the sink made its own client, waiting its ShutdownTimeout at most.</summary>
    public void Dispose()
    {
        if (ownsClient) client?.Dispose();
    }

#if NET
    /// <summary>Sends what's queued, if the sink made its own client, waiting its ShutdownTimeout at most.</summary>
    public System.Threading.Tasks.ValueTask DisposeAsync() =>
        ownsClient && client != null ? client.DisposeAsync() : default;
#endif

    internal static LumberLevel Level(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose or LogEventLevel.Debug => LumberLevel.Debug,
        LogEventLevel.Information => LumberLevel.Info,
        LogEventLevel.Warning => LumberLevel.Warn,
        LogEventLevel.Error => LumberLevel.Error,
        _ => LumberLevel.Fatal,
    };

    // Strings as they are rather than quoted, and structures as JSON, as Serilog's console sink writes them by default
    private static MessageTemplateTextFormatter Formatter(IFormatProvider? formatProvider) =>
        new("{Message:lj}", formatProvider);

    private static object? Simplify(LogEventPropertyValue value) => value switch
    {
        ScalarValue scalar => scalar.Value,
        SequenceValue sequence => sequence.Elements.Select(Simplify).ToList(),
        StructureValue structure => structure.Properties.ToDictionary(property => property.Name, property => Simplify(property.Value), StringComparer.Ordinal),
        DictionaryValue dictionary => dictionary.Elements.ToDictionary(
            pair => pair.Key.Value?.ToString() ?? "",
            pair => Simplify(pair.Value),
            StringComparer.Ordinal),
        _ => value.ToString(),
    };
}
