# InfiniteToken.Lumber

.NET client for [Lumber](https://lumber.infinitetoken.com): cloud logging with end-to-end encryption. Every log is encrypted with AES-256-GCM before it leaves your process, so Lumber's servers store only ciphertext plus each log's level (kept readable for filtering and push alerts). You read your logs in the Lumber app, which decrypts them on your device.

No dependencies beyond the base library. Logs are sent in the background, so logging never waits on the network, and it plugs into `ILogger` (ASP.NET Core, worker services, Azure Functions) and Serilog.

## Installation

```bash
dotnet add package InfiniteToken.Lumber
```

For `ILogger` and ASP.NET Core, or for Serilog, add one of these instead (each brings the client with it):

```bash
dotnet add package InfiniteToken.Lumber.Extensions.Logging
```

```bash
dotnet add package InfiniteToken.Lumber.Serilog
```

.NET 8 or later, .NET Framework 4.6.2 or later, or anything else that runs .NET Standard 2.0.

## Quick start

```csharp
using InfiniteToken.Lumber;

using var lumber = new LumberClient(Environment.GetEnvironmentVariable("LUMBER_API_KEY"));

lumber.Info("Server started", new { port = 8080 });
lumber.Warn("Rate limit approaching", new { RequestId = "req-123", userId = 456 });
lumber.Error(exception, "Payment failed", new { TraceId = "trace-abc", orderId = 789 });
```

Make one client and share it: it's safe to use from any thread. Disposing it sends the logs still queued, waiting 5 seconds at most, and so does the process exiting.

## Getting your API key

Create an app in the Lumber app and it takes you straight to adding an API key. The key is shown only once, so copy it right away. Any device holding the app's key can add more from the app's **Secrets** screen, one per deployment if you like.

An API key is two values joined by a dot: a token, then the app's encryption key. The client sends only the token to Lumber and encrypts every log with the rest, so the encryption key never leaves your process. Keep the API key in an environment variable or your secrets manager, never in source control.

## ASP.NET Core and `ILogger`

`InfiniteToken.Lumber.Extensions.Logging` is a logging provider, so everything that already logs through `ILogger`, your code and the framework's, goes to Lumber too:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddLumber(options => options.Environment = builder.Environment.EnvironmentName);

var app = builder.Build();
app.MapPost("/pay", (Order order, ILogger<Program> logger) =>
{
    logger.LogInformation("Charging {OrderId} for {Total}", order.Id, order.Total);
    return Results.Ok();
});
app.Run();
```

The API key comes from the `LUMBER_API_KEY` environment variable or configuration value (so `dotnet user-secrets set LUMBER_API_KEY <key>` works in development), or from `Logging:Lumber:ApiKey`. With no key anywhere, the provider warns once and sends nothing, so a machine without one keeps logging to the console as usual.

Which levels go to Lumber is set like any provider's, under its alias, `Lumber`. In `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": { "Default": "Information" },
    "Lumber": {
      "LogLevel": { "Default": "Warning", "MyApp": "Information" },
      "Environment": "Production"
    }
  }
}
```

Each log is sent at the matching level (`Trace` and `Debug` are DEBUG, `Critical` is FATAL). Its message is the formatted message, and the template's values (`OrderId`, `Total`) go into its metadata. So do the values of the logger's scopes, which in ASP.NET Core include each request's: its `RequestId` and `TraceId` become the log's own request and trace ids, and `RequestPath` goes into metadata. The logger's category goes into the context as `logger`, an exception into metadata as `exception` and `stackTrace`, and an event id as `eventId` and `eventName`. `IncludeScopes = false` leaves scopes out.

Worker services and console apps on the generic host add it the same way, with `builder.Logging.AddLumber()`. Without a host, make a factory and dispose it before exiting:

```csharp
using var factory = LoggerFactory.Create(logging => logging.AddLumber());
var logger = factory.CreateLogger("Importer");
```

To log through `ILogger` and call the client yourself with one client, pass it in, and dispose it yourself:

```csharp
var lumber = new LumberClient(builder.Configuration["LUMBER_API_KEY"]);
builder.Services.AddSingleton(lumber);
builder.Logging.AddLumber(lumber);
```

## With Serilog

`InfiniteToken.Lumber.Serilog` is a Serilog sink:

```csharp
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.Lumber(Environment.GetEnvironmentVariable("LUMBER_API_KEY"), environment: "Production")
    .CreateLogger();

Log.Information("Report ready for {Customer} in {Elapsed} ms", "acme", 2400);
Log.ForContext("RequestId", "req-123").Error(exception, "Payment failed for {@Order}", order);

Log.CloseAndFlush(); // sends the logs still queued
```

Each event is sent at the matching level (`Verbose` is DEBUG). Its message is rendered as Serilog's console sink renders it, and its properties go into the log's metadata, structures as nested objects. `RequestId` and `TraceId` are the log's own request and trace ids, `SourceContext` (from `ForContext<T>()`) goes into its context as `logger`, and when an `Activity` is running, its trace and span ids are sent too. `apiKey` defaults to the `LUMBER_API_KEY` environment variable, and every argument can come from `appsettings.json` through Serilog.Settings.Configuration:

```json
{
  "Serilog": {
    "WriteTo": [{ "Name": "Lumber", "Args": { "environment": "Production", "restrictedToMinimumLevel": "Information" } }]
  }
}
```

`WriteTo.Lumber(options => ...)` takes every option below, and `WriteTo.Lumber(lumber)` a client you share with your own calls.

## Log methods

| Method | Level |
|--------|-------|
| `Debug(message, data)` | DEBUG |
| `Info(message, data)` | INFO |
| `Warn(message, data)` | WARN |
| `Error(message, data)` | ERROR |
| `Fatal(message, data)` | FATAL. It doesn't end the process |
| `Log(level, message, data)` | Any level, as a `LumberLevel` |
| `Flush(timeout)`, `FlushAsync(timeout, cancellationToken)` | Waits for queued logs to send: `false` if `timeout` passed first |
| `Dispose()`, `DisposeAsync()` | Sends what's queued, waiting `ShutdownTimeout` at most, and stops |

Each log method also takes an exception first: `lumber.Error(exception, "Payment failed", data)`. Logging never throws and never blocks.

## Data

The second argument is the log's data: an anonymous object, a dictionary, or any object, whose properties become the log's fields.

```csharp
lumber.Info("User signed in", new
{
    RequestId = "req-abc",                // the log's request ID (requestId works too)
    TraceId = "trace-xyz",                // the log's trace ID (traceId works too)
    context = new { region = "us-east" }, // merged into its context
    metadata = new { plan = "pro" },      // merged into its metadata
    userId = 123,                         // any other property goes into metadata
});
```

An explicit `metadata` wins over a colliding property. An exception goes into metadata as `exception` (its type and message) and `stackTrace` (the whole of `ToString()`, inner exceptions included). Values JSON has no type for are sent as text (a `DateTime` as ISO 8601, an enum as its name, a `Guid`, `TimeSpan` or `Uri` as you'd write it), a NaN as null, and a structure that contains itself as `"[Circular]"`. Everything, message included, is encrypted.

## Options

```csharp
var lumber = new LumberClient(new LumberOptions
{
    ApiKey = Environment.GetEnvironmentVariable("LUMBER_API_KEY"),
    Environment = "Production",                 // sent as `env` in every log's context
    IncludeCaller = true,                       // send the file, line and method that logged
    AutoContext = false,                        // don't send the process id, hostname and environment
    Timeout = TimeSpan.FromSeconds(10),         // how long to wait for Lumber to answer one log
    MaxQueue = 10_000,                          // logs waiting to send before new ones are dropped
    ShutdownTimeout = TimeSpan.FromSeconds(5),  // how long queued logs get on Dispose or exit
    HttpClient = httpClient,                    // your own HttpClient, for a proxy or custom TLS
    OnWarning = message => Console.WriteLine(message), // the client's warnings, in place of standard error
});
```

Unless set, `Environment` is the `ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT` environment variable, when either is set. The `ILogger` provider's options are the same, plus `IncludeScopes`, and can all come from `Logging:Lumber` in configuration.

## Delivery

Logs are sent in order, one `POST /logs` per log, over connections kept open between logs. A network error, a 5xx or no answer within `Timeout` counts as a failed attempt: each log gets 5, waiting 1, 2, 4, then 8 seconds between them, and is then dropped with a warning. A log Lumber refuses (a 4xx, such as an invalid API key, or a 413 for a log over 16 KB once encrypted) is dropped at once with a warning, since sending it again won't help. Errors always get through: past your team's monthly logs, Lumber still takes error and fatal logs, and answers the rest 200 without keeping them, so the client carries on with no warning (the Lumber app shows when your team is at its limit). At twice your team's monthly logs Lumber answers 429: the client warns once and drops logs until the month turns. While Lumber can't be reached, up to `MaxQueue` logs wait; past that, new ones are dropped. Warnings go to standard error unless you pass `OnWarning`. Never send them back into Lumber through a logger, or one failure becomes many.

## Serverless

A serverless platform can freeze an instance as soon as it returns, with logs still queued. On AWS Lambda, wait for them before returning, with a time limit so a slow answer can't hold the function to its maximum duration:

```csharp
public async Task<APIGatewayProxyResponse> Handle(APIGatewayProxyRequest request, ILambdaContext context)
{
    lumber.Info("Handled request", new { path = request.Path });
    await lumber.FlushAsync(TimeSpan.FromSeconds(10));
    return new APIGatewayProxyResponse { StatusCode = 200 };
}
```

Make the client once, outside the handler, so warm invocations reuse it and its connection. Logging through `ILogger` there, share a client with `AddLumber(lumber)` so you can flush it. Azure Functions' isolated worker runs between invocations like any other process, so its logs send in the background, and the host flushes them when it shuts down.

## Development

```bash
dotnet test
```

The .NET 10 SDK builds every target. Tests run on .NET 10 and 8, and on .NET Framework 4.8 on Windows, which runs the .NET Standard 2.0 build, with its own AES-GCM (.NET Standard has no `AesGcm`). CI runs them on Linux, macOS and Windows.

`tests/contract` holds [Lumber-Kit](https://github.com/infinitetoken/lumber-kit)'s contract at the version in `tests/contract/VERSION`: a JSON Schema of the wire format and known answers for API keys, encryption, payloads and delivery, which Lumber's Node, Python and Go clients are tested against too. `scripts/sync-contract.sh 0.8.0` moves it to a new kit version.

## License

MIT. See `LICENSE`.
