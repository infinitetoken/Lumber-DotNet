using System;
using System.Net.Http;

namespace InfiniteToken.Lumber;

/// <summary>Configures a <see cref="LumberClient"/>.</summary>
public class LumberOptions
{
    /// <summary>
    /// The app's API key from its Secrets screen in Lumber, <c>&lt;token&gt;.&lt;encryptionKey&gt;</c>. Only the token is
    /// sent to Lumber; the encryption key encrypts every log and never leaves the process.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>The Lumber API logs go to, for testing against another server.</summary>
    public string Endpoint { get; set; } = LumberClient.DefaultEndpoint;

    /// <summary>
    /// Sent with every log as <c>env</c> in its context (Production, Staging). Unset, it's the
    /// <c>ASPNETCORE_ENVIRONMENT</c> or <c>DOTNET_ENVIRONMENT</c> environment variable, when either is set.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>Send the process id, hostname and environment with every log, in its context. On unless turned off.</summary>
    public bool AutoContext { get; set; } = true;

    /// <summary>
    /// Also send the file, line and method that called a log method (<c>Info</c> and the rest), in the log's context.
    /// Off unless turned on.
    /// </summary>
    public bool IncludeCaller { get; set; }

    /// <summary>How long to wait for Lumber to answer one log: 10 seconds unless set.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many logs can wait to send, while Lumber can't be reached, before new ones are dropped: 10,000 unless set.</summary>
    public int MaxQueue { get; set; } = 10_000;

    /// <summary>
    /// How long queued logs get to send when the client is disposed or the process exits: 5 seconds unless set.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends logs with your own HttpClient (a proxy, custom TLS), which the client never disposes. Its own Timeout
    /// applies as well as <see cref="Timeout"/>.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// Receives the client's warnings (a log refused, Lumber unreachable) in place of standard error. Pass a method that
    /// does nothing to silence them. Never route them back into Lumber.
    /// </summary>
    public Action<string>? OnWarning { get; set; }
}
