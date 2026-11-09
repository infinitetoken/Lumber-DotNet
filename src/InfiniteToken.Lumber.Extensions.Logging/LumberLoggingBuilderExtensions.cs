using System;
using InfiniteToken.Lumber;
using InfiniteToken.Lumber.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;

// Beside AddConsole and the rest, so builder.Logging.AddLumber() needs no using
namespace Microsoft.Extensions.Logging;

/// <summary>Adds Lumber to an <see cref="ILoggingBuilder"/>.</summary>
public static class LumberLoggingBuilderExtensions
{
    /// <summary>
    /// Sends logs to Lumber. Options come from the <c>Logging:Lumber</c> configuration section; the API key from
    /// <c>Logging:Lumber:ApiKey</c>, or else the <c>LUMBER_API_KEY</c> configuration value or environment variable. With
    /// no API key anywhere, it warns once and sends nothing.
    /// </summary>
    /// <param name="builder">The logging builder (<c>builder.Logging</c> in ASP.NET Core).</param>
    /// <returns>The builder.</returns>
    public static ILoggingBuilder AddLumber(this ILoggingBuilder builder)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        builder.AddConfiguration();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, LumberLoggerProvider>(
            services => new LumberLoggerProvider(services.GetRequiredService<IOptions<LumberLoggerOptions>>().Value)));
        LoggerProviderOptions.RegisterProviderOptions<LumberLoggerOptions, LumberLoggerProvider>(builder.Services);
        builder.Services.AddOptions<LumberLoggerOptions>().PostConfigure<IServiceProvider>((options, services) =>
        {
            if (!string.IsNullOrWhiteSpace(options.ApiKey)) return;
            var fromConfiguration = services.GetService<IConfiguration>()?["LUMBER_API_KEY"];
            options.ApiKey = string.IsNullOrWhiteSpace(fromConfiguration)
                ? Environment.GetEnvironmentVariable("LUMBER_API_KEY")
                : fromConfiguration;
        });
        return builder;
    }

    /// <summary>
    /// Sends logs to Lumber, with options set in code over those from configuration (see <see cref="AddLumber(ILoggingBuilder)"/>).
    /// </summary>
    /// <param name="builder">The logging builder (<c>builder.Logging</c> in ASP.NET Core).</param>
    /// <param name="configure">Sets the options: <c>options =&gt; options.Environment = "Production"</c>.</param>
    /// <returns>The builder.</returns>
    public static ILoggingBuilder AddLumber(this ILoggingBuilder builder, Action<LumberLoggerOptions> configure)
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        builder.AddLumber();
        builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>Sends logs to Lumber with a client you made, and dispose yourself, shared with your own log calls.</summary>
    /// <param name="builder">The logging builder (<c>builder.Logging</c> in ASP.NET Core).</param>
    /// <param name="client">The client to log with.</param>
    /// <param name="includeScopes">Send the values of the logger's scopes with each log.</param>
    /// <returns>The builder.</returns>
    public static ILoggingBuilder AddLumber(this ILoggingBuilder builder, LumberClient client, bool includeScopes = true)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (client == null) throw new ArgumentNullException(nameof(client));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, LumberLoggerProvider>(
            _ => new LumberLoggerProvider(client, includeScopes)));
        return builder;
    }
}
