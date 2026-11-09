using System;
using InfiniteToken.Lumber;
using InfiniteToken.Lumber.Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

// Serilog's sinks put their WriteTo methods in Serilog's own namespace
namespace Serilog;

/// <summary>Adds the Lumber sink to Serilog's <c>WriteTo</c>.</summary>
public static class LumberLoggerConfigurationExtensions
{
    /// <summary>
    /// Sends events to Lumber. Every argument can come from appsettings.json through Serilog.Settings.Configuration:
    /// <c>{ "Name": "Lumber", "Args": { "environment": "Production" } }</c>.
    /// </summary>
    /// <param name="writeTo">Serilog's <c>WriteTo</c>.</param>
    /// <param name="apiKey">The app's API key from its Secrets screen in Lumber: the <c>LUMBER_API_KEY</c> environment variable unless given.</param>
    /// <param name="environment">Sent with every log as <c>env</c> in its context (Production, Staging).</param>
    /// <param name="endpoint">The Lumber API, for testing against another server.</param>
    /// <param name="restrictedToMinimumLevel">The lowest level sent to Lumber.</param>
    /// <param name="levelSwitch">Changes the lowest level sent while running.</param>
    /// <param name="formatProvider">Formats values in the message: the current culture unless given.</param>
    /// <returns>The logger configuration.</returns>
    public static LoggerConfiguration Lumber(
        this LoggerSinkConfiguration writeTo,
        string? apiKey = null,
        string? environment = null,
        string? endpoint = null,
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        LoggingLevelSwitch? levelSwitch = null,
        IFormatProvider? formatProvider = null) =>
        writeTo.Lumber(
            options =>
            {
                options.ApiKey = apiKey;
                options.Environment = environment;
                if (!string.IsNullOrWhiteSpace(endpoint)) options.Endpoint = endpoint!;
            },
            restrictedToMinimumLevel,
            levelSwitch,
            formatProvider);

    /// <summary>Sends events to Lumber, with every option set in code.</summary>
    /// <param name="writeTo">Serilog's <c>WriteTo</c>.</param>
    /// <param name="configure">Sets the options. With no API key, the <c>LUMBER_API_KEY</c> environment variable is used.</param>
    /// <param name="restrictedToMinimumLevel">The lowest level sent to Lumber.</param>
    /// <param name="levelSwitch">Changes the lowest level sent while running.</param>
    /// <param name="formatProvider">Formats values in the message: the current culture unless given.</param>
    /// <returns>The logger configuration.</returns>
    public static LoggerConfiguration Lumber(
        this LoggerSinkConfiguration writeTo,
        Action<LumberOptions> configure,
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        LoggingLevelSwitch? levelSwitch = null,
        IFormatProvider? formatProvider = null)
    {
        if (writeTo == null) throw new ArgumentNullException(nameof(writeTo));
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        var options = new LumberOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey)) options.ApiKey = Environment.GetEnvironmentVariable("LUMBER_API_KEY");
        return writeTo.Sink(new LumberSink(options, formatProvider), restrictedToMinimumLevel, levelSwitch);
    }

    /// <summary>Sends events to Lumber with a client you made, and dispose yourself, shared with your own log calls.</summary>
    /// <param name="writeTo">Serilog's <c>WriteTo</c>.</param>
    /// <param name="client">The client to log with.</param>
    /// <param name="restrictedToMinimumLevel">The lowest level sent to Lumber.</param>
    /// <param name="levelSwitch">Changes the lowest level sent while running.</param>
    /// <param name="formatProvider">Formats values in the message: the current culture unless given.</param>
    /// <returns>The logger configuration.</returns>
    public static LoggerConfiguration Lumber(
        this LoggerSinkConfiguration writeTo,
        LumberClient client,
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        LoggingLevelSwitch? levelSwitch = null,
        IFormatProvider? formatProvider = null)
    {
        if (writeTo == null) throw new ArgumentNullException(nameof(writeTo));
        return writeTo.Sink(new LumberSink(client, formatProvider), restrictedToMinimumLevel, levelSwitch);
    }
}
