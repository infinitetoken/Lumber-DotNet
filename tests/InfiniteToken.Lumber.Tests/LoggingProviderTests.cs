using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using InfiniteToken.Lumber.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfiniteToken.Lumber.Tests;

public class LoggingProviderTests
{
    [Fact]
    public void SendsILoggerLogs()
    {
        var api = new FakeLumber();
        using (var factory = Factory(api))
        {
            var logger = factory.CreateLogger("Billing.Jobs");
            logger.LogInformation("Report ready in {Seconds}s for {Rows} rows", 2.4, 1500);
            logger.LogDebug("Not sent: below Information");
        }

        var payload = Assert.Single(api.Requests).Payload;
        JsonAssert.Equal(
            "{\"level\":1,\"message\":\"Report ready in 2.4s for 1500 rows\",\"context\":{\"logger\":\"Billing.Jobs\"},\"metadata\":{\"Seconds\":2.4,\"Rows\":1500}}",
            payload);
        ContractFiles.Conforms("LogPayload", payload);
    }

    [Fact]
    public void EachLevel()
    {
        var api = new FakeLumber();
        using (var factory = Factory(api, LogLevel.Trace))
        {
            var logger = factory.CreateLogger("Levels");
            logger.LogTrace("t");
            logger.LogDebug("d");
            logger.LogInformation("i");
            logger.LogWarning("w");
            logger.LogError("e");
            logger.LogCritical("c");
            logger.Log(LogLevel.None, "never");
        }
        Assert.Equal(new[] { 0, 0, 1, 2, 3, 4 }, api.Requests.Select(request => request.Json.GetProperty("level").GetInt32()));
    }

    [Fact]
    public void ScopesExceptionsAndEvents()
    {
        var api = new FakeLumber();
        using (var factory = Factory(api))
        {
            var logger = factory.CreateLogger<LoggingProviderTests>();
            // What ASP.NET Core opens around a request
            using (logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = "0HN7:00000001", ["RequestPath"] = "/pay", ["TraceId"] = "4bf92f3577b34da6a3ce929d0e0e4736" }))
            using (logger.BeginScope("Order {OrderId}", 789))
            using (logger.BeginScope("checkout"))
            {
                logger.LogError(new EventId(42, "PaymentFailed"), new InvalidOperationException("Declined"), "Payment failed for {Customer}", "jay");
            }
            logger.LogWarning("Outside the scopes");
        }

        var payload = api.Requests[0].Payload;
        Assert.Equal("Payment failed for jay", payload.GetProperty("message").GetString());
        Assert.Equal("0HN7:00000001", payload.GetProperty("requestId").GetString());
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", payload.GetProperty("traceId").GetString());
        Assert.Equal(typeof(LoggingProviderTests).FullName, payload.GetProperty("context").GetProperty("logger").GetString());
        var metadata = payload.GetProperty("metadata");
        Assert.Equal("/pay", metadata.GetProperty("RequestPath").GetString());
        Assert.Equal(789, metadata.GetProperty("OrderId").GetInt32());
        Assert.Equal("jay", metadata.GetProperty("Customer").GetString());
        Assert.Equal(42, metadata.GetProperty("eventId").GetInt32());
        Assert.Equal("PaymentFailed", metadata.GetProperty("eventName").GetString());
        Assert.Equal("System.InvalidOperationException: Declined", metadata.GetProperty("exception").GetString());
        Assert.Equal("checkout", Assert.Single(metadata.GetProperty("scope").EnumerateArray()).GetString());
        Assert.False(metadata.TryGetProperty("{OriginalFormat}", out _));
        ContractFiles.Conforms("LogPayload", payload);

        var outside = api.Requests[1].Payload;
        Assert.False(outside.TryGetProperty("requestId", out _));
        Assert.False(outside.TryGetProperty("metadata", out _));
    }

    [Fact]
    public void ScopesCanBeTurnedOff()
    {
        var api = new FakeLumber();
        using (var factory = Factory(api, configure: options => options.IncludeScopes = false))
        {
            var logger = factory.CreateLogger("NoScopes");
            using (logger.BeginScope("Order {OrderId}", 789)) logger.LogInformation("Hello");
        }
        Assert.False(api.Requests[0].Payload.TryGetProperty("metadata", out _));
    }

    [Fact]
    public void OptionsAndFiltersFromConfiguration()
    {
        var api = new FakeLumber();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:Lumber:ApiKey"] = TestKeys.ApiKey,
            ["Logging:Lumber:Environment"] = "Staging",
            ["Logging:Lumber:Endpoint"] = "http://localhost:3000",
            ["Logging:Lumber:LogLevel:Default"] = "Warning",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddLumber(options =>
            {
                options.HttpClient = new HttpClient(api);
                options.OnWarning = _ => { };
            });
        });
        using (var provider = services.BuildServiceProvider())
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Configured");
            logger.LogInformation("Filtered out");
            logger.LogWarning("Sent");
        }

        var request = Assert.Single(api.Requests);
        Assert.Equal("http://localhost:3000/logs", request.Url);
        Assert.Equal("Staging", request.Payload.GetProperty("context").GetProperty("env").GetString());
        Assert.Equal("Sent", request.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public void TheApiKeyFromConfigurationsLumberApiKey()
    {
        var api = new FakeLumber();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LUMBER_API_KEY"] = TestKeys.ApiKey }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.AddLumber(options => options.HttpClient = new HttpClient(api)));
        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Keyed").LogWarning("Sent");
        }
        Assert.Single(api.Requests);
    }

    [Fact]
    public void NoApiKeyWarnsOnceAndSendsNothing()
    {
        var warnings = new List<string>();
        var previous = Environment.GetEnvironmentVariable("LUMBER_API_KEY");
        Environment.SetEnvironmentVariable("LUMBER_API_KEY", null);
        try
        {
            using var factory = LoggerFactory.Create(logging => logging.AddLumber(options => options.OnWarning = warnings.Add));
            var logger = factory.CreateLogger("Unkeyed");
            Assert.False(logger.IsEnabled(LogLevel.Critical));
            logger.LogCritical("Nowhere");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LUMBER_API_KEY", previous);
        }
        Assert.Equal(new[] { "No API key, so logs aren't sent to Lumber. Set LUMBER_API_KEY, or Logging:Lumber:ApiKey in configuration." }, warnings);
    }

    [Fact]
    public void ASharedClient()
    {
        using var test = new TestClient();
        using (var factory = LoggerFactory.Create(logging => logging.AddLumber(test.Client)))
        {
            factory.CreateLogger("Shared").LogInformation("Through ILogger");
        }
        test.Client.Info("Directly, after the factory's gone");
        test.Flush();
        Assert.Equal(new[] { "Through ILogger", "Directly, after the factory's gone" }, test.Api.Requests.Select(request => request.Payload.GetProperty("message").GetString()));
    }

    private static ILoggerFactory Factory(FakeLumber api, LogLevel minimum = LogLevel.Information, Action<LumberLoggerOptions>? configure = null) =>
        LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(minimum);
            logging.AddLumber(options =>
            {
                options.ApiKey = TestKeys.ApiKey;
                options.AutoContext = false;
                options.HttpClient = new HttpClient(api);
                options.OnWarning = _ => { };
                configure?.Invoke(options);
            });
        });
}
