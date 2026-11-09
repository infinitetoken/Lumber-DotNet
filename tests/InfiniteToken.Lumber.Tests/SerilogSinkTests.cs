using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using Serilog;
using Serilog.Events;
using Xunit;

namespace InfiniteToken.Lumber.Tests;

public class SerilogSinkTests
{
    [Fact]
    public void SendsSerilogEvents()
    {
        var api = new FakeLumber();
        using (var logger = Logger(api))
        {
            logger.ForContext<SerilogSinkTests>().Information("Report ready for {Customer} with {@Order} and {Tags}", "jay", new { Id = 7, Total = 12.5 }, new[] { "a", "b" });
        }

        var payload = Assert.Single(api.Requests).Payload;
        Assert.Equal("Report ready for jay with {\"Id\":7,\"Total\":12.5} and [\"a\",\"b\"]", payload.GetProperty("message").GetString());
        Assert.Equal(typeof(SerilogSinkTests).FullName, payload.GetProperty("context").GetProperty("logger").GetString());
        JsonAssert.Equal("{\"Customer\":\"jay\",\"Order\":{\"Id\":7,\"Total\":12.5},\"Tags\":[\"a\",\"b\"]}", payload.GetProperty("metadata"));
        ContractFiles.Conforms("LogPayload", payload);
    }

    [Fact]
    public void EachLevel()
    {
        var api = new FakeLumber();
        using (var logger = Logger(api))
        {
            logger.Verbose("v");
            logger.Debug("d");
            logger.Information("i");
            logger.Warning("w");
            logger.Error("e");
            logger.Fatal("f");
        }
        Assert.Equal(new[] { 0, 0, 1, 2, 3, 4 }, api.Requests.Select(request => request.Json.GetProperty("level").GetInt32()));
    }

    [Fact]
    public void RequestIdTraceIdAndAnException()
    {
        var api = new FakeLumber();
        using (var logger = Logger(api))
        {
            logger.ForContext("RequestId", "req-123")
                .ForContext("TraceId", "trace-abc")
                .Error(new InvalidOperationException("Declined"), "Payment failed for order {OrderId}", 789);
        }

        var payload = api.Requests[0].Payload;
        Assert.Equal("req-123", payload.GetProperty("requestId").GetString());
        Assert.Equal("trace-abc", payload.GetProperty("traceId").GetString());
        var metadata = payload.GetProperty("metadata");
        Assert.Equal(789, metadata.GetProperty("OrderId").GetInt32());
        Assert.Equal("System.InvalidOperationException: Declined", metadata.GetProperty("exception").GetString());
        Assert.Contains("Declined", metadata.GetProperty("stackTrace").GetString());
    }

    [Fact]
    public void TheCurrentActivitysTrace()
    {
        var api = new FakeLumber();
        using var activity = new Activity("checkout").SetIdFormat(ActivityIdFormat.W3C).Start();
        using (var logger = Logger(api))
        {
            logger.Information("Inside a trace");
        }
        activity.Stop();

        var payload = api.Requests[0].Payload;
        Assert.Equal(activity.TraceId.ToHexString(), payload.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), payload.GetProperty("metadata").GetProperty("spanId").GetString());
    }

    [Fact]
    public void ASharedClient()
    {
        using var test = new TestClient();
        using (var logger = new LoggerConfiguration().WriteTo.Lumber(test.Client).CreateLogger())
        {
            logger.Information("Through Serilog");
        }
        test.Client.Info("Directly, after the logger's gone");
        test.Flush();
        Assert.Equal(new[] { "Through Serilog", "Directly, after the logger's gone" }, test.Api.Requests.Select(request => request.Payload.GetProperty("message").GetString()));
    }

    [Fact]
    public void NoApiKeyWarnsOnceAndSendsNothing()
    {
        var warnings = new List<string>();
        var previous = Environment.GetEnvironmentVariable("LUMBER_API_KEY");
        Environment.SetEnvironmentVariable("LUMBER_API_KEY", null);
        try
        {
            using var logger = new LoggerConfiguration().WriteTo.Lumber(options => options.OnWarning = warnings.Add).CreateLogger();
            logger.Fatal("Nowhere");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LUMBER_API_KEY", previous);
        }
        Assert.Equal(new[] { "No API key, so logs aren't sent to Lumber. Set LUMBER_API_KEY, or pass apiKey to WriteTo.Lumber." }, warnings);
    }

    [Fact]
    public void TheLowestLevel()
    {
        var api = new FakeLumber();
        using (var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Lumber(Configure(api), restrictedToMinimumLevel: LogEventLevel.Warning)
            .CreateLogger())
        {
            logger.Information("Not sent");
            logger.Warning("Sent");
        }
        Assert.Equal("Sent", Assert.Single(api.Requests).Payload.GetProperty("message").GetString());
    }

    private static global::Serilog.Core.Logger Logger(FakeLumber api) =>
        new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Lumber(Configure(api)).CreateLogger();

    private static Action<LumberOptions> Configure(FakeLumber api) => options =>
    {
        options.ApiKey = TestKeys.ApiKey;
        options.AutoContext = false;
        options.HttpClient = new HttpClient(api);
        options.OnWarning = _ => { };
    };
}
