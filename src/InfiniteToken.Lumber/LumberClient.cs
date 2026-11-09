using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace InfiniteToken.Lumber;

/// <summary>
/// Encrypts logs with an app's key and sends them to Lumber, cloud logging with end-to-end encryption. Every log is
/// encrypted with AES-256-GCM before it leaves the process, so Lumber's servers store only ciphertext plus each log's
/// level.
/// <code>
/// using var lumber = new LumberClient(Environment.GetEnvironmentVariable("LUMBER_API_KEY"));
/// lumber.Info("Server started", new { port = 8080 });
/// </code>
/// Logging never blocks or throws: logs are queued and sent in the background. Disposing the client sends what's
/// queued, waiting <see cref="LumberOptions.ShutdownTimeout"/> at most, and so does the process exiting. Safe for
/// concurrent use; make one and share it.
/// </summary>
public sealed class LumberClient : IDisposable
#if NET
    , IAsyncDisposable
#endif
{
    /// <summary>The Lumber API logs go to unless <see cref="LumberOptions.Endpoint"/> says otherwise.</summary>
    public const string DefaultEndpoint = Contract.DefaultEndpoint;

    private readonly Sender sender;
    private readonly LogCipher cipher;
    private readonly Dictionary<string, object?> autoContext;
    private readonly bool includeCaller;
    private readonly TimeSpan shutdownTimeout;
    private readonly Action<string>? onWarning;
    private int disposed;

    /// <summary>Makes a client from an app's API key, <c>&lt;token&gt;.&lt;encryptionKey&gt;</c>, with the default options.</summary>
    /// <param name="apiKey">The API key from the app's Secrets screen in Lumber.</param>
    /// <exception cref="ArgumentException">The API key is missing or isn't one.</exception>
    public LumberClient(string? apiKey)
        : this(new LumberOptions { ApiKey = apiKey })
    {
    }

    /// <summary>Makes a client from options, which need at least <see cref="LumberOptions.ApiKey"/>.</summary>
    /// <exception cref="ArgumentException">The API key is missing or isn't one, or an option is out of range.</exception>
    public LumberClient(LumberOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        Contract.SplitApiKey(options.ApiKey, out var token, out var encryptionKey);
        if (token.Length == 0)
        {
            throw new ArgumentException("Missing Lumber API key. Create one on your app's Secrets screen in Lumber.", nameof(options));
        }
        if (string.IsNullOrEmpty(encryptionKey))
        {
            throw new ArgumentException(
                "The Lumber API key has no encryption key. Copy a new API key from your app's Secrets screen in Lumber, which includes it.",
                nameof(options));
        }
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encryptionKey);
        }
        catch (FormatException)
        {
            key = Array.Empty<byte>();
        }
        if (key.Length != Contract.KeyBytes)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The Lumber API key's encryption key should be {0} bytes of base64, got {1}. Copy the API key again from your app's Secrets screen in Lumber.",
                    Contract.KeyBytes,
                    key.Length),
                nameof(options));
        }
        var endpoint = (options.Endpoint ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint + Contract.IngestPath, UriKind.Absolute, out var url) || (url.Scheme != "https" && url.Scheme != "http"))
        {
            throw new ArgumentException("The Lumber endpoint must be an http or https URL, got \"" + options.Endpoint + "\".", nameof(options));
        }
        if (options.Timeout <= TimeSpan.Zero && options.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be more than zero.");
        }
        if (options.ShutdownTimeout < TimeSpan.Zero && options.ShutdownTimeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ShutdownTimeout can't be negative.");
        }

        onWarning = options.OnWarning;
        includeCaller = options.IncludeCaller;
        shutdownTimeout = options.ShutdownTimeout;
        autoContext = options.AutoContext ? AutoContext(options.Environment) : new Dictionary<string, object?>();
        cipher = LogCipher.Create(key);
        var http = options.HttpClient ?? OwnHttpClient();
        var userAgent = "lumber-dotnet/" + Version + " dotnet/" + System.Environment.Version.ToString();
        sender = new Sender(http, options.HttpClient == null, url, token, userAgent, options.Timeout, Math.Max(1, options.MaxQueue), cipher, Warn);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>This package's version, sent in the User-Agent.</summary>
    public static string Version { get; } = ReadVersion();

    internal Sender Sender => sender;

    // --- Logging ---

    /// <summary>Logs at DEBUG.</summary>
    /// <param name="message">The log's message.</param>
    /// <param name="data">
    /// The log's data: an anonymous object, a dictionary or any object, whose properties go into the log's metadata,
    /// except <c>requestId</c> and <c>traceId</c> (or <c>RequestId</c> and <c>TraceId</c>), the log's own fields,
    /// <c>context</c>, merged into its context, and <c>metadata</c>, merged into its metadata.
    /// </param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Debug(string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Debug, message, null, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at DEBUG, with an exception.</summary>
    /// <param name="exception">Sent in the log's metadata as <c>exception</c> (its type and message) and <c>stackTrace</c>.</param>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Debug(Exception? exception, string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Debug, message, exception, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at INFO.</summary>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Info(string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Info, message, null, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at INFO, with an exception.</summary>
    /// <param name="exception">Sent in the log's metadata as <c>exception</c> (its type and message) and <c>stackTrace</c>.</param>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Info(Exception? exception, string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Info, message, exception, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at WARN.</summary>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Warn(string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Warn, message, null, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at WARN, with an exception.</summary>
    /// <param name="exception">Sent in the log's metadata as <c>exception</c> (its type and message) and <c>stackTrace</c>.</param>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Warn(Exception? exception, string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Warn, message, exception, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at ERROR.</summary>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Error(string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Error, message, null, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at ERROR, with an exception.</summary>
    /// <param name="exception">Sent in the log's metadata as <c>exception</c> (its type and message) and <c>stackTrace</c>.</param>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Error(Exception? exception, string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Error, message, exception, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at FATAL. It doesn't end the process: dispose the client first if the process is about to end.</summary>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Fatal(string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Fatal, message, null, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at FATAL, with an exception. It doesn't end the process.</summary>
    /// <param name="exception">Sent in the log's metadata as <c>exception</c> (its type and message) and <c>stackTrace</c>.</param>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Fatal(Exception? exception, string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(LumberLevel.Fatal, message, exception, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at <paramref name="level"/>.</summary>
    /// <param name="level">The log's level.</param>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Log(LumberLevel level, string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(level, message, null, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>Logs at <paramref name="level"/>, with an exception.</summary>
    /// <param name="level">The log's level.</param>
    /// <param name="exception">Sent in the log's metadata as <c>exception</c> (its type and message) and <c>stackTrace</c>.</param>
    /// <param name="message">The log's message.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="callerMemberName">Filled in by the compiler; sent when <see cref="LumberOptions.IncludeCaller"/> is on.</param>
    /// <param name="callerFilePath">Filled in by the compiler.</param>
    /// <param name="callerLineNumber">Filled in by the compiler.</param>
    public void Log(LumberLevel level, Exception? exception, string message, object? data = null, [CallerMemberName] string? callerMemberName = null, [CallerFilePath] string? callerFilePath = null, [CallerLineNumber] int callerLineNumber = 0) =>
        Write(level, message, exception, data, Caller(callerMemberName, callerFilePath, callerLineNumber));

    /// <summary>
    /// Logs with no caller information, for a logging library's adapter (the Microsoft.Extensions.Logging provider and the
    /// Serilog sink use it). Never throws.
    /// </summary>
    /// <param name="level">The log's level.</param>
    /// <param name="message">The log's message.</param>
    /// <param name="exception">An exception, sent in the log's metadata as <c>exception</c> and <c>stackTrace</c>.</param>
    /// <param name="data">The log's data. See <see cref="Debug(string, object, string, string, int)"/>.</param>
    /// <param name="context">
    /// Context from the logging library (the logger's name), merged over the automatic context and under the data's own
    /// <c>context</c>.
    /// </param>
    public void Write(LumberLevel level, string? message, Exception? exception = null, object? data = null, object? context = null)
    {
        try
        {
            if (exception == null && data is Exception given)
            {
                exception = given;
                data = null;
            }
            var normalized = Json.Normalize(data);
            var fields = normalized as Dictionary<string, object?>
                ?? (normalized == null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["data"] = normalized });
            // .NET's names for the log's own fields
            Rename(fields, "RequestId", "requestId");
            Rename(fields, "TraceId", "traceId");
            if (exception != null)
            {
                if (!fields.ContainsKey("exception")) fields["exception"] = Json.ExceptionLine(exception);
                if (!fields.ContainsKey("stackTrace")) fields["stackTrace"] = exception.ToString();
            }

            var auto = autoContext;
            if (Json.Normalize(context) is Dictionary<string, object?> extra && extra.Count > 0)
            {
                auto = new Dictionary<string, object?>(autoContext);
                foreach (var pair in extra) auto[pair.Key] = pair.Value;
            }

            var wire = Math.Min((int)LumberLevel.Fatal, Math.Max((int)LumberLevel.Debug, (int)level));
            sender.Enqueue(wire, Json.Serialize(Contract.BuildPayload(wire, message ?? "", fields, auto)));
        }
        catch (Exception error)
        {
            Warn("couldn't log \"" + message + "\": " + error.Message);
        }
    }

    // --- Delivery ---

    /// <summary>Waits until every queued log is sent (or dropped).</summary>
    /// <param name="timeout">The longest to wait; no limit unless given.</param>
    /// <returns>False when the time ran out first.</returns>
    public bool Flush(TimeSpan? timeout = null) => sender.Flush(Limit(timeout));

    /// <summary>Waits until every queued log is sent (or dropped).</summary>
    /// <param name="timeout">The longest to wait; no limit unless given.</param>
    /// <param name="cancellationToken">Stops waiting, throwing OperationCanceledException.</param>
    /// <returns>False when the time ran out first.</returns>
    public Task<bool> FlushAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        sender.FlushAsync(Limit(timeout), cancellationToken);

    /// <summary>
    /// Sends what's queued, waiting <see cref="LumberOptions.ShutdownTimeout"/> at most, and stops. Logs after this are
    /// dropped.
    /// </summary>
    public void Dispose()
    {
        if (!BeginDispose()) return;
        if (!sender.Close(shutdownTimeout)) Warn("closed with logs still waiting to send");
        cipher.Dispose();
    }

#if NET
    /// <summary>
    /// Sends what's queued, waiting <see cref="LumberOptions.ShutdownTimeout"/> at most, and stops. Logs after this are
    /// dropped.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose()) return;
        if (!await sender.CloseAsync(shutdownTimeout).ConfigureAwait(false)) Warn("closed with logs still waiting to send");
        cipher.Dispose();
    }
#endif

    // --- Internals ---

    private bool BeginDispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return false;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        return true;
    }

    // Queued logs get ShutdownTimeout to send when the process exits without disposing the client
    private void OnProcessExit(object? source, EventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0 && !sender.Flush(shutdownTimeout)) Warn("exiting with logs still waiting to send");
    }

    private void Warn(string message)
    {
        try
        {
            if (onWarning != null) onWarning(message);
            else Console.Error.WriteLine("[lumber] " + message);
        }
        catch (Exception)
        {
            // A warning can't be allowed to stop logging
        }
    }

    private Dictionary<string, object?>? Caller(string? method, string? file, int line) =>
        includeCaller && !string.IsNullOrEmpty(file)
            ? new Dictionary<string, object?> { ["code"] = file, ["line"] = line, ["method"] = method }
            : null;

    private static void Rename(Dictionary<string, object?> fields, string name, string wire)
    {
        if (!fields.TryGetValue(name, out var value)) return;
        fields.Remove(name);
        if (value != null && !fields.ContainsKey(wire)) fields[wire] = value;
    }

    private static TimeSpan Limit(TimeSpan? timeout) =>
        timeout is { } given && given != System.Threading.Timeout.InfiniteTimeSpan
            ? (given < TimeSpan.Zero ? TimeSpan.Zero : given)
            : System.Threading.Timeout.InfiniteTimeSpan;

    private static Dictionary<string, object?> AutoContext(string? environment)
    {
        var context = new Dictionary<string, object?>();
        try
        {
#if NET
            context["pid"] = System.Environment.ProcessId;
#else
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            context["pid"] = process.Id;
#endif
        }
        catch (Exception)
        {
            // Not every platform will say
        }
        var hostname = Hostname();
        if (!string.IsNullOrEmpty(hostname)) context["hostname"] = hostname;
        var env = environment;
        if (string.IsNullOrEmpty(env)) env = System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrEmpty(env)) env = System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.IsNullOrEmpty(env)) context["env"] = env;
        return context;
    }

    private static string? Hostname()
    {
        try
        {
            return System.Net.Dns.GetHostName();
        }
        catch (Exception)
        {
            try
            {
                return System.Environment.MachineName;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static HttpClient OwnHttpClient()
    {
#if NET
        // Connections are replaced every few minutes so a DNS change reaches a long-running process
        if (SocketsHttpHandler.IsSupported)
        {
            return new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
        }
#endif
        return new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    private static string ReadVersion()
    {
        var version = typeof(LumberClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(LumberClient).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version.Substring(0, plus);
    }
}
