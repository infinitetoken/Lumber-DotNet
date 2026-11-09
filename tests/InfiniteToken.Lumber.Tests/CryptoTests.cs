using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace InfiniteToken.Lumber.Tests;

public class CryptoTests
{
#if NET
    /// <summary>
    /// The managed AES-GCM (the netstandard2.0 build's, for .NET Framework and Mono) gives the same bytes as the
    /// platform's AesGcm, for every length around the block size and a few long ones.
    /// </summary>
    [Fact]
    public void ManagedMatchesThePlatform()
    {
        var random = new Random(42);
        var lengths = Enumerable.Range(0, 70).Concat(new[] { 255, 256, 257, 1000, 4096, 65_537 });
        foreach (var length in lengths)
        {
            var key = Bytes(random, 32);
            var nonce = Bytes(random, 12);
            var plaintext = Bytes(random, length);
            using var platform = new System.Security.Cryptography.AesGcm(key, 16);
            var expected = new byte[16 + length];
            platform.Encrypt(nonce, plaintext, expected.AsSpan(16), expected.AsSpan(0, 16));

            using var managed = new ManagedAesGcm(key);
            Assert.Equal(expected, managed.Seal(nonce, plaintext));
            Assert.Equal(plaintext, managed.Open(nonce, expected));
        }
    }
#endif

    [Fact]
    public void ManagedRefusesATamperedLog()
    {
        using var managed = new ManagedAesGcm(TestKeys.Key);
        var nonce = new byte[12];
        var sealedBytes = managed.Seal(nonce, Encoding.UTF8.GetBytes("{\"level\":1,\"message\":\"hi\"}"));
        foreach (var index in new[] { 0, 15, 16, sealedBytes.Length - 1 })
        {
            var tampered = (byte[])sealedBytes.Clone();
            tampered[index] ^= 1;
            Assert.Null(managed.Open(nonce, tampered));
        }
        Assert.Null(managed.Open(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, sealedBytes));
    }

    [Fact]
    public void EveryLogGetsItsOwnNonce()
    {
        using var test = new TestClient();
        for (var i = 0; i < 50; i++) test.Client.Info("Same message");
        test.Flush();
        var nonces = test.Api.Requests.Select(request => request.Json.GetProperty("encrypted").GetProperty("nonce").GetString()).ToList();
        Assert.Equal(50, nonces.Count);
        Assert.Equal(50, nonces.Distinct().Count());
        Assert.All(nonces, nonce => Assert.Equal(12, Convert.FromBase64String(nonce!).Length));
    }

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
