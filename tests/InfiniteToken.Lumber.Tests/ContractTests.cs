using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InfiniteToken.Lumber.Tests;

/// <summary>Every known answer in Lumber-Kit's contract, against this client's own code.</summary>
public class ContractTests
{
    public static TheoryData<string> ApiKeys => new(ContractFiles.Names("apiKeys"));

    public static TheoryData<string> Encryptions => new(ContractFiles.Names("encryption"));

    public static TheoryData<string> Payloads => new(ContractFiles.Names("payloads"));

    [Fact]
    public void Constants()
    {
        var constants = ContractFiles.Vectors.GetProperty("constants");
        Assert.Equal(LumberClient.DefaultEndpoint, constants.GetProperty("defaultEndpoint").GetString());
        Assert.Equal(Contract.IngestPath, constants.GetProperty("ingestPath").GetString());
        Assert.Equal(Contract.KeyVersion, constants.GetProperty("keyVersion").GetInt32());
        Assert.Equal(Contract.KeyBytes, constants.GetProperty("keyBytes").GetInt32());
        Assert.Equal(Contract.NonceBytes, constants.GetProperty("nonceBytes").GetInt32());
        Assert.Equal(Contract.TagBytes, constants.GetProperty("tagBytes").GetInt32());

        var levels = constants.GetProperty("levels").EnumerateObject().ToDictionary(level => level.Name, level => level.Value.GetInt32());
        var ours = Enum.GetValues(typeof(LumberLevel)).Cast<LumberLevel>().ToDictionary(level => level.ToString().ToLowerInvariant(), level => (int)level);
        Assert.Equal(levels.OrderBy(pair => pair.Key), ours.OrderBy(pair => pair.Key));

        var delivery = constants.GetProperty("delivery");
        Assert.Equal(Contract.MaxAttempts, delivery.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(TimeSpan.FromMilliseconds(delivery.GetProperty("baseDelayMs").GetInt32()), Contract.BaseDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(delivery.GetProperty("maxDelayMs").GetInt32()), Contract.MaxDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(delivery.GetProperty("pauseMs").GetInt32()), Contract.PauseFor);
    }

    [Theory]
    [MemberData(nameof(ApiKeys))]
    public void SplitsApiKeys(string name)
    {
        var vector = ContractFiles.Vector("apiKeys", name);
        Contract.SplitApiKey(vector.GetProperty("apiKey").GetString(), out var token, out var encryptionKey);
        Assert.Equal(vector.GetProperty("token").GetString(), token);
        Assert.Equal(vector.GetProperty("encryptionKey").ValueKind == JsonValueKind.Null ? null : vector.GetProperty("encryptionKey").GetString(), encryptionKey);
    }

    [Theory]
    [MemberData(nameof(Encryptions))]
    public void Encrypts(string name)
    {
        var vector = ContractFiles.Vector("encryption", name);
        var key = Convert.FromBase64String(vector.GetProperty("key").GetString()!);
        var nonce = Convert.FromBase64String(vector.GetProperty("nonce").GetString()!);
        var plaintext = Encoding.UTF8.GetBytes(vector.GetProperty("plaintext").GetString()!);
        var expected = vector.GetProperty("payload").GetString();

        using (var cipher = LogCipher.Create(key))
        {
            Assert.Equal(expected, Convert.ToBase64String(cipher.Seal(nonce, plaintext)));
        }
        // The netstandard2.0 build's own AES-GCM, whatever this runtime uses
        using var managed = new ManagedAesGcm(key);
        var sealedBytes = managed.Seal(nonce, plaintext);
        Assert.Equal(expected, Convert.ToBase64String(sealedBytes));
        Assert.Equal(plaintext, managed.Open(nonce, sealedBytes));
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void BuildsPayloads(string name)
    {
        var vector = ContractFiles.Vector("payloads", name);
        var payload = Contract.BuildPayload(
            vector.GetProperty("level").GetInt32(),
            vector.GetProperty("message").GetString()!,
            Object(vector, "data"),
            Object(vector, "autoContext"));
        var json = JsonAssert.Parse(Json.Serialize(payload));
        JsonAssert.Equal(vector.GetProperty("payload"), json);
        ContractFiles.Conforms("LogPayload", json);
    }

    /// <summary>
    /// Through the whole client: what it sends is a LogIngestBody whose plaintext is the vector's payload. The automatic
    /// context goes in as Write's context, with the client's own turned off.
    /// </summary>
    [Theory]
    [MemberData(nameof(Payloads))]
    public void ClientSendsPayloads(string name)
    {
        var vector = ContractFiles.Vector("payloads", name);
        using var test = new TestClient();
        test.Client.Write(
            (LumberLevel)vector.GetProperty("level").GetInt32(),
            vector.GetProperty("message").GetString(),
            null,
            Object(vector, "data"),
            Object(vector, "autoContext"));
        test.Flush();

        var request = Assert.Single(test.Api.Requests);
        var body = request.Json;
        ContractFiles.Conforms("LogIngestBody", body);
        Assert.Equal(vector.GetProperty("level").GetInt32(), body.GetProperty("level").GetInt32());
        var encrypted = body.GetProperty("encrypted");
        Assert.Equal(ContractFiles.Vectors.GetProperty("constants").GetProperty("keyVersion").GetInt32(), encrypted.GetProperty("keyVersion").GetInt32());
        Assert.Equal(Contract.NonceBytes, Convert.FromBase64String(encrypted.GetProperty("nonce").GetString()!).Length);
        JsonAssert.Equal(vector.GetProperty("payload"), request.Payload);
        Assert.Empty(test.Warnings);
    }

    [Fact]
    public void DeliveryOutcomes()
    {
        foreach (var vector in ContractFiles.Vectors.GetProperty("deliveryOutcomes").EnumerateArray())
        {
            var status = vector.GetProperty("status");
            int? code = status.ValueKind == JsonValueKind.Null ? null : status.GetInt32();
            Assert.Equal(vector.GetProperty("outcome").GetString(), Contract.Outcome(code).ToString().ToLowerInvariant());
        }
    }

    [Fact]
    public void RetryDelays()
    {
        foreach (var vector in ContractFiles.Vectors.GetProperty("retryDelaysMs").EnumerateArray())
        {
            Assert.Equal(TimeSpan.FromMilliseconds(vector.GetProperty("delayMs").GetInt32()), Contract.RetryDelay(vector.GetProperty("failedAttempts").GetInt32()));
        }
        Assert.Equal(Contract.MaxDelay, Contract.RetryDelay(200));
        Assert.Equal(Contract.BaseDelay, Contract.RetryDelay(0));
    }

    // A vector's object as the normalized values the client's code takes, or null when it has none
    private static Dictionary<string, object?>? Object(JsonElement vector, string property) =>
        vector.TryGetProperty(property, out var value) ? (Dictionary<string, object?>)Json.Parse(value.GetRawText())! : null;
}
