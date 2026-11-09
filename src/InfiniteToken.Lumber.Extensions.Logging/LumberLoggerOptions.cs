namespace InfiniteToken.Lumber.Extensions.Logging;

/// <summary>
/// Configures the Lumber logger provider. Bound from the <c>Logging:Lumber</c> configuration section (appsettings.json),
/// then from the action given to <c>AddLumber</c>. With no <see cref="LumberOptions.ApiKey"/>, the provider reads the
/// <c>LUMBER_API_KEY</c> configuration value or environment variable.
/// </summary>
public class LumberLoggerOptions : LumberOptions
{
    /// <summary>Send the values of the logger's scopes (ASP.NET Core's RequestId, TraceId and RequestPath) with each log. On unless turned off.</summary>
    public bool IncludeScopes { get; set; } = true;
}
