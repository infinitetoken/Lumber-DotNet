using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace InfiniteToken.Lumber.Tests;

public class ClientTests
{
    [Fact]
    public void SendsOnlyTheTokenToTheApi()
    {
        using var test = new TestClient();
        test.Client.Info("Server started", new { port = 8080 });
        test.Flush();

        var request = Assert.Single(test.Api.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.lumber.infinitetoken.com/logs", request.Url);
        Assert.Equal("Bearer " + TestKeys.Token, request.Authorization);
        Assert.Equal("application/json", request.ContentType);
        Assert.StartsWith("lumber-dotnet/" + LumberClient.Version + " dotnet/", request.UserAgent);
        Assert.DoesNotContain(Convert.ToBase64String(TestKeys.Key), System.Text.Encoding.UTF8.GetString(request.Body));
        Assert.DoesNotContain("Server started", System.Text.Encoding.UTF8.GetString(request.Body));
        Assert.Equal(1, request.Json.GetProperty("level").GetInt32());
        JsonAssert.Equal("{\"level\":1,\"message\":\"Server started\",\"metadata\":{\"port\":8080}}", request.Payload);
    }

    [Fact]
    public void EachLevel()
    {
        using var test = new TestClient();
        test.Client.Debug("d");
        test.Client.Info("i");
        test.Client.Warn("w");
        test.Client.Error("e");
        test.Client.Fatal("f");
        test.Client.Log(LumberLevel.Warn, "l");
        test.Client.Write((LumberLevel)99, "too high");
        test.Client.Write((LumberLevel)(-3), "too low");
        test.Flush();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 2, 4, 0 }, test.Api.Requests.Select(request => request.Json.GetProperty("level").GetInt32()));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 2, 4, 0 }, test.Api.Requests.Select(request => request.Payload.GetProperty("level").GetInt32()));
        Assert.Equal(new[] { "d", "i", "w", "e", "f", "l", "too high", "too low" }, test.Api.Requests.Select(request => request.Payload.GetProperty("message").GetString()));
    }

    [Fact]
    public void ADotNetRequestIdAndTraceIdAreTheLogsOwn()
    {
        using var test = new TestClient();
        test.Client.Warn("Rate limit approaching", new { RequestId = "req-123", TraceId = "trace-abc", userId = 456 });
        test.Client.Warn("Both spellings", new Dictionary<string, object?> { ["requestId"] = "wire", ["RequestId"] = "dotnet" });
        test.Client.Warn("A number", new { requestId = 42 });
        test.Flush();
        JsonAssert.Equal(
            "{\"level\":2,\"message\":\"Rate limit approaching\",\"requestId\":\"req-123\",\"traceId\":\"trace-abc\",\"metadata\":{\"userId\":456}}",
            test.Api.Requests[0].Payload);
        JsonAssert.Equal("{\"level\":2,\"message\":\"Both spellings\",\"requestId\":\"wire\"}", test.Api.Requests[1].Payload);
        JsonAssert.Equal("{\"level\":2,\"message\":\"A number\",\"requestId\":\"42\"}", test.Api.Requests[2].Payload);
    }

    [Fact]
    public void AnException()
    {
        using var test = new TestClient();
        Exception error;
        try
        {
            throw new InvalidOperationException("Payment declined", new TimeoutException("Bank slow"));
        }
        catch (Exception caught)
        {
            error = caught;
        }
        test.Client.Error(error, "Payment failed", new { orderId = 789 });
        test.Client.Error("Passed as data", error);
        test.Flush();

        var metadata = test.Api.Requests[0].Payload.GetProperty("metadata");
        Assert.Equal(789, metadata.GetProperty("orderId").GetInt32());
        Assert.Equal("System.InvalidOperationException: Payment declined", metadata.GetProperty("exception").GetString());
        var stackTrace = metadata.GetProperty("stackTrace").GetString()!;
        Assert.Contains("Bank slow", stackTrace);
        Assert.Contains(nameof(AnException), stackTrace);
        Assert.Equal("System.InvalidOperationException: Payment declined", test.Api.Requests[1].Payload.GetProperty("metadata").GetProperty("exception").GetString());
    }

    [Fact]
    public void DataThatIsntAnObject()
    {
        using var test = new TestClient();
        test.Client.Info("A list", new[] { 1, 2, 3 });
        test.Client.Info("Text", "just text");
        test.Flush();
        JsonAssert.Equal("{\"level\":1,\"message\":\"A list\",\"metadata\":{\"data\":[1,2,3]}}", test.Api.Requests[0].Payload);
        JsonAssert.Equal("{\"level\":1,\"message\":\"Text\",\"metadata\":{\"data\":\"just text\"}}", test.Api.Requests[1].Payload);
    }

    [Fact]
    public void AutomaticContext()
    {
        using var test = new TestClient(options =>
        {
            options.AutoContext = true;
            options.Environment = "staging";
        });
        test.Client.Info("Job done", new { context = new { hostname = "worker-3", line = 42 } });
        test.Flush();
        var context = test.Api.Requests[0].Payload.GetProperty("context");
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, context.GetProperty("pid").GetInt32());
        Assert.Equal("worker-3", context.GetProperty("hostname").GetString());
        Assert.Equal("staging", context.GetProperty("env").GetString());
        Assert.Equal(42, context.GetProperty("line").GetInt32());
    }

    [Fact]
    public void TheHostname()
    {
        using var test = new TestClient(options => options.AutoContext = true);
        test.Client.Info("Hello");
        test.Flush();
        Assert.Equal(System.Net.Dns.GetHostName(), test.Api.Requests[0].Payload.GetProperty("context").GetProperty("hostname").GetString());
    }

    [Fact]
    public void TheCaller()
    {
        using var test = new TestClient(options => options.IncludeCaller = true);
        test.Client.Info("Here");
        test.Client.Write(LumberLevel.Info, "From an adapter");
        test.Flush();
        var context = test.Api.Requests[0].Payload.GetProperty("context");
        Assert.EndsWith("ClientTests.cs", context.GetProperty("code").GetString());
        Assert.Equal(nameof(TheCaller), context.GetProperty("method").GetString());
        Assert.True(context.GetProperty("line").GetInt32() > 0);
        Assert.False(test.Api.Requests[1].Payload.TryGetProperty("context", out _));
    }

    [Fact]
    public void NoCallerUnlessAskedFor()
    {
        using var test = new TestClient();
        test.Client.Info("Here");
        test.Flush();
        Assert.False(test.Api.Requests[0].Payload.TryGetProperty("context", out _));
    }

    [Fact]
    public void AnEndpointOfYourOwn()
    {
        using var test = new TestClient(options => options.Endpoint = "http://localhost:3000/");
        test.Client.Info("Local");
        test.Flush();
        Assert.Equal("http://localhost:3000/logs", test.Api.Requests[0].Url);
    }

    [Theory]
    [InlineData(null, "Missing Lumber API key")]
    [InlineData("  ", "Missing Lumber API key")]
    [InlineData(TestKeys.Token, "has no encryption key")]
    [InlineData(TestKeys.Token + ".", "has no encryption key")]
    [InlineData(TestKeys.Token + ".not base64!", "should be 32 bytes of base64, got 0")]
    [InlineData(TestKeys.Token + ".AAECAw==", "should be 32 bytes of base64, got 4")]
    public void RefusesABadApiKey(string? apiKey, string message)
    {
        var error = Assert.Throws<ArgumentException>(() => new LumberClient(apiKey));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("")]
    public void RefusesABadEndpoint(string endpoint)
    {
        var error = Assert.Throws<ArgumentException>(() => new LumberClient(new LumberOptions { ApiKey = TestKeys.ApiKey, Endpoint = endpoint }));
        Assert.Contains("http or https URL", error.Message);
    }

    [Fact]
    public void RefusesNoTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LumberClient(new LumberOptions { ApiKey = TestKeys.ApiKey, Timeout = TimeSpan.Zero }));
    }

    [Fact]
    public void TakesAPastedKey()
    {
        using var client = new LumberClient("  " + TestKeys.ApiKey + "\n");
    }

    [Fact]
    public void LoggingNeverThrows()
    {
        using var test = new TestClient();
        test.Client.Info(null!, new Exploding());
        test.Client.Info("Still logging");
        test.Flush();
        Assert.Equal(2, test.Api.Requests.Count);
        JsonAssert.Equal("{\"level\":1,\"message\":\"\",\"metadata\":{\"Boom\":\"[Unreadable: boom]\"}}", test.Api.Requests[0].Payload);
    }

    [Fact]
    public void AWarningCallbackThatThrowsDoesntStopLogging()
    {
        var test = new TestClient(options => options.OnWarning = _ => throw new InvalidOperationException("bad callback"));
        test.Api.Respond = (_, count, _) => System.Threading.Tasks.Task.FromResult(FakeLumber.Answer(count == 1 ? 400 : 201));
        test.Client.Info("Refused");
        test.Client.Info("Sent");
        test.Flush();
        Assert.Equal(2, test.Api.Requests.Count);
        test.Dispose();
    }

    [Fact]
    public void AfterDisposeLogsAreDropped()
    {
        var test = new TestClient();
        test.Client.Info("Before");
        test.Dispose();
        test.Client.Info("After");
        Assert.True(test.Client.Flush(TimeSpan.FromSeconds(1)));
        Assert.Equal(new[] { "Before" }, test.Api.Requests.Select(request => request.Payload.GetProperty("message").GetString()));
        test.Dispose();
    }

    [Fact]
    public void YourOwnHttpClientIsntDisposed()
    {
        var api = new FakeLumber();
        var http = new HttpClient(api);
        using (var client = new LumberClient(new LumberOptions { ApiKey = TestKeys.ApiKey, HttpClient = http, OnWarning = _ => { } }))
        {
            client.Info("Hello");
        }
        Assert.Single(api.Requests);
        // Still usable
        using var again = new LumberClient(new LumberOptions { ApiKey = TestKeys.ApiKey, HttpClient = http, OnWarning = _ => { } });
        again.Info("Again");
        Assert.True(again.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public void ThePayloadConforms()
    {
        using var test = new TestClient(options => options.AutoContext = true);
        test.Client.Error(new InvalidOperationException("x"), "Everything", new { RequestId = "r", TraceId = "t", context = new { code = "a.cs" }, metadata = new { retries = 3 }, when = DateTime.UtcNow });
        test.Flush();
        ContractFiles.Conforms("LogIngestBody", test.Api.Requests[0].Json);
        ContractFiles.Conforms("LogPayload", test.Api.Requests[0].Payload);
    }

    [Fact]
    public void TheSchemaCatchesAMisspelledField()
    {
        Assert.ThrowsAny<Exception>(() => ContractFiles.Conforms("LogPayload", JsonAssert.Parse("{\"level\":1,\"message\":\"x\",\"request_id\":\"r\"}")));
    }

    private sealed class Exploding
    {
        public string Boom => throw new InvalidOperationException("boom");
    }
}
