using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace InfiniteToken.Lumber.Extensions.Logging;

/// <summary>
/// A Microsoft.Extensions.Logging provider that sends logs to Lumber. Add it with
/// <c>builder.Logging.AddLumber()</c>. Its alias is <c>Lumber</c>, so <c>Logging:Lumber:LogLevel</c> in appsettings.json
/// sets which levels go to Lumber.
/// </summary>
[ProviderAlias("Lumber")]
public sealed class LumberLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, LumberLogger> loggers = new(StringComparer.Ordinal);
    private readonly bool ownsClient;
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

    /// <summary>A provider with its own client, made from <paramref name="options"/>, which it disposes with itself.</summary>
    /// <param name="options">The client's options. With no API key, the provider warns once and sends nothing.</param>
    public LumberLoggerProvider(LumberLoggerOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        IncludeScopes = options.IncludeScopes;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            // Logging stays on everywhere else: a machine without the key (a laptop, CI) just doesn't send to Lumber
            const string warning = "No API key, so logs aren't sent to Lumber. Set LUMBER_API_KEY, or Logging:Lumber:ApiKey in configuration.";
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
        Client = new LumberClient(options);
        ownsClient = true;
    }

    /// <summary>A provider that logs with a client you made and dispose yourself.</summary>
    /// <param name="client">The client to log with.</param>
    /// <param name="includeScopes">Send the values of the logger's scopes with each log.</param>
    public LumberLoggerProvider(LumberClient client, bool includeScopes = true)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        IncludeScopes = includeScopes;
    }

    internal LumberClient? Client { get; }

    internal bool IncludeScopes { get; }

    internal IExternalScopeProvider Scopes => scopes;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        loggers.GetOrAdd(categoryName ?? "", name => new LumberLogger(name, this));

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider ?? scopes;

    /// <summary>Sends what's queued, if the provider made its own client, waiting its ShutdownTimeout at most.</summary>
    public void Dispose()
    {
        if (ownsClient) Client?.Dispose();
    }
}

internal sealed class LumberLogger : ILogger
{
    private const string OriginalFormat = "{OriginalFormat}";
    private readonly string category;
    private readonly LumberLoggerProvider provider;

    public LumberLogger(string category, LumberLoggerProvider provider)
    {
        this.category = category;
        this.provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => provider.Scopes.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && provider.Client != null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var client = provider.Client;
        if (client == null || logLevel == LogLevel.None) return;
        string? message;
        try
        {
            message = formatter != null ? formatter(state, exception) : state?.ToString();
        }
        catch (Exception error)
        {
            message = "[Unformattable: " + error.Message + "]";
        }

        // Scopes first, outermost to innermost, then the log's own values over them
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (provider.IncludeScopes) provider.Scopes.ForEachScope(AddScope, data);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var pair in values)
            {
                if (pair.Key != OriginalFormat) data[pair.Key] = pair.Value;
            }
        }
        if (eventId.Id != 0 && !data.ContainsKey("eventId")) data["eventId"] = eventId.Id;
        if (!string.IsNullOrEmpty(eventId.Name) && !data.ContainsKey("eventName")) data["eventName"] = eventId.Name;

        client.Write(Level(logLevel), message, exception, data, new Dictionary<string, object?> { ["logger"] = category });
    }

    internal static LumberLevel Level(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => LumberLevel.Debug,
        LogLevel.Information => LumberLevel.Info,
        LogLevel.Warning => LumberLevel.Warn,
        LogLevel.Error => LumberLevel.Error,
        _ => LumberLevel.Fatal,
    };

    // A scope of values (BeginScope("Order {OrderId}", id), ASP.NET Core's RequestId) adds them; any other scope (a
    // string) goes into a `scope` list
    private static void AddScope(object? scope, Dictionary<string, object?> data)
    {
        if (scope is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var pair in values)
            {
                if (pair.Key != OriginalFormat) data[pair.Key] = pair.Value;
            }
            return;
        }
        if (scope == null) return;
        if (!(data.TryGetValue("scope", out var existing) && existing is List<object?> list))
        {
            list = new List<object?>();
            data["scope"] = list;
        }
        list.Add(scope);
    }
}
