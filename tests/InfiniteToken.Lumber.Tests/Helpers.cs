using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Json.Schema;
using Xunit;

namespace InfiniteToken.Lumber.Tests;

/// <summary>
/// Lumber-Kit's contract (tests/contract, synced by scripts/sync-contract.sh): the same schema and known answers
/// Lumber-Node, Lumber-Python and Lumber-Go are tested against.
/// </summary>
internal static class ContractFiles
{
    public static readonly JsonElement Vectors = Load("vectors.json");
    public static readonly JsonElement Schema = Load("schema.json");

    public static JsonElement Vector(string group, string name) =>
        Vectors.GetProperty(group).EnumerateArray().Single(vector => vector.GetProperty("name").GetString() == name);

    public static IEnumerable<string> Names(string group) =>
        Vectors.GetProperty(group).EnumerateArray().Select(vector => vector.GetProperty("name").GetString()!);

    /// <summary>Fails unless <paramref name="value"/> is a valid <paramref name="definition"/> in the kit's schema.</summary>
    public static void Conforms(string definition, JsonElement value)
    {
        var text = "{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"definitions\":"
            + Schema.GetProperty("definitions").GetRawText()
            + ",\"allOf\":[{\"$ref\":\"#/definitions/" + definition + "\"}]}";
        var schema = JsonSchema.FromText(text);
        var results = schema.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(results.IsValid, definition + " doesn't conform: " + value.GetRawText());
    }

    private static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", name))).RootElement;
}

internal static class TestKeys
{
    public static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
    public const string Token = "0123456789abcdef0123456789abcdef";
    public static readonly string ApiKey = Token + "." + Convert.ToBase64String(Key);
}

/// <summary>Compares JSON by meaning: objects by keys whatever their order, numbers by value.</summary>
internal static class JsonAssert
{
    public static void Equal(JsonElement expected, JsonElement actual) =>
        Assert.True(Same(expected, actual), "Expected " + expected.GetRawText() + "\nbut got  " + actual.GetRawText());

    public static void Equal(string expected, JsonElement actual) => Equal(Parse(expected), actual);

    public static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement;

    public static JsonElement Parse(byte[] utf8) => JsonDocument.Parse(utf8).RootElement;

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var left = a.EnumerateObject().ToList();
                var right = b.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
                return left.Count == right.Count && left.All(property => right.TryGetValue(property.Name, out var other) && Same(property.Value, other));
            case JsonValueKind.Array:
                var leftItems = a.EnumerateArray().ToList();
                var rightItems = b.EnumerateArray().ToList();
                return leftItems.Count == rightItems.Count && leftItems.Zip(rightItems, Same).All(same => same);
            case JsonValueKind.Number:
                return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : a.GetDouble().Equals(b.GetDouble());
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            default:
                return true;
        }
    }
}

internal static class Crypto
{
    /// <summary>Opens a POST /logs body's <c>encrypted</c> with the key: the log's plaintext payload.</summary>
    public static JsonElement Decrypt(JsonElement body, byte[]? key = null)
    {
        var encrypted = body.GetProperty("encrypted");
        var nonce = Convert.FromBase64String(encrypted.GetProperty("nonce").GetString()!);
        var sealedBytes = Convert.FromBase64String(encrypted.GetProperty("payload").GetString()!);
        return JsonAssert.Parse(Open(key ?? TestKeys.Key, nonce, sealedBytes));
    }

    public static byte[] Open(byte[] key, byte[] nonce, byte[] sealedBytes)
    {
#if NET
        using var aes = new AesGcm(key, 16);
        var plaintext = new byte[sealedBytes.Length - 16];
        aes.Decrypt(nonce, sealedBytes.AsSpan(16), sealedBytes.AsSpan(0, 16), plaintext);
        return plaintext;
#else
        using var aes = new ManagedAesGcm(key);
        return aes.Open(nonce, sealedBytes) ?? throw new CryptographicException("The tag doesn't match");
#endif
    }
}

/// <summary>A request the fake API received.</summary>
internal sealed class Received
{
    public Received(HttpRequestMessage request, byte[] body)
    {
        Method = request.Method.Method;
        Url = request.RequestUri!.ToString();
        Authorization = request.Headers.Authorization?.ToString();
        UserAgent = request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null;
        ContentType = request.Content?.Headers.ContentType?.MediaType;
        Body = body;
    }

    public string Method { get; }

    public string Url { get; }

    public string? Authorization { get; }

    public string? UserAgent { get; }

    public string? ContentType { get; }

    public byte[] Body { get; }

    public JsonElement Json => JsonAssert.Parse(Body);

    public JsonElement Payload => Crypto.Decrypt(Json);
}

/// <summary>Lumber's API as an HttpMessageHandler: it records each request and answers 201 unless told otherwise.</summary>
internal sealed class FakeLumber : HttpMessageHandler
{
    private readonly ConcurrentQueue<Received> received = new();

    public Func<Received, int, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));

    public IReadOnlyList<Received> Requests => received.ToArray();

    public static HttpResponseMessage Answer(int status, string? message = null, string? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (message != null) response.Content = new StringContent("{\"message\":" + JsonSerializer.Serialize(message) + "}", Encoding.UTF8, "application/json");
        if (retryAfter != null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var captured = new Received(request, body);
        received.Enqueue(captured);
        return await Respond(captured, received.Count, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A client against a <see cref="FakeLumber"/>, with its warnings collected and its retries quick.</summary>
internal sealed class TestClient : IDisposable
{
    public TestClient(Action<LumberOptions>? configure = null)
    {
        var options = new LumberOptions
        {
            ApiKey = TestKeys.ApiKey,
            AutoContext = false,
            HttpClient = new HttpClient(Api),
            OnWarning = Warnings.Enqueue,
        };
        configure?.Invoke(options);
        Client = new LumberClient(options);
        Client.Sender.RetryDelay = _ => TimeSpan.FromMilliseconds(1);
    }

    public FakeLumber Api { get; } = new();

    public LumberClient Client { get; }

    public ConcurrentQueue<string> Warnings { get; } = new();

    public void Flush() => Assert.True(Client.Flush(TimeSpan.FromSeconds(10)), "the logs didn't send in 10 seconds");

    public void Dispose() => Client.Dispose();
}
