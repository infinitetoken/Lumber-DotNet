using System;
using System.Security.Cryptography;

namespace InfiniteToken.Lumber;

/// <summary>
/// AES-256-GCM in Lumber's layout: the 16-byte auth tag, then the ciphertext. On .NET 8 and later it's the base
/// library's <c>AesGcm</c>. .NET Standard 2.0 (.NET Framework, Mono) has no AesGcm, so there it's
/// <see cref="ManagedAesGcm"/>: GCM's counter mode and GHASH over the platform's own AES. Safe for concurrent use.
/// </summary>
internal abstract class LogCipher : IDisposable
{
    public static LogCipher Create(byte[] key)
    {
#if NET
        if (AesGcm.IsSupported) return new PlatformAesGcm(key);
#endif
        return new ManagedAesGcm(key);
    }

    /// <summary>Encrypts <paramref name="plaintext"/> under <paramref name="nonce"/>: the tag, then the ciphertext.</summary>
    public abstract byte[] Seal(byte[] nonce, byte[] plaintext);

    public abstract void Dispose();
}

#if NET
internal sealed class PlatformAesGcm : LogCipher
{
    private readonly AesGcm aes;

    public PlatformAesGcm(byte[] key) => aes = new AesGcm(key, Contract.TagBytes);

    public override byte[] Seal(byte[] nonce, byte[] plaintext)
    {
        var sealedBytes = new byte[Contract.TagBytes + plaintext.Length];
        lock (aes)
        {
            aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(Contract.TagBytes), sealedBytes.AsSpan(0, Contract.TagBytes));
        }
        return sealedBytes;
    }

    public override void Dispose() => aes.Dispose();
}
#endif

/// <summary>
/// AES-GCM (NIST SP 800-38D) with a 96-bit nonce and a 128-bit tag, built on AES in ECB mode, which every .NET has. Only
/// what Lumber needs: no associated data. GHASH multiplies without branching on secret bits.
/// </summary>
internal sealed class ManagedAesGcm : LogCipher
{
    private const int Block = 16;
    private readonly Aes aes;
    private readonly ICryptoTransform encryptor;
    private readonly ulong hashKeyHigh;
    private readonly ulong hashKeyLow;
    private readonly object gate = new();

    public ManagedAesGcm(byte[] key)
    {
        if (key.Length != Contract.KeyBytes) throw new ArgumentException("AES-256-GCM takes a 32-byte key", nameof(key));
        aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        encryptor = aes.CreateEncryptor();
        var hashKey = new byte[Block];
        encryptor.TransformBlock(new byte[Block], 0, Block, hashKey, 0);
        hashKeyHigh = ReadUInt64(hashKey, 0);
        hashKeyLow = ReadUInt64(hashKey, 8);
    }

    public override byte[] Seal(byte[] nonce, byte[] plaintext)
    {
        var sealedBytes = new byte[Contract.TagBytes + plaintext.Length];
        lock (gate)
        {
            var tagMask = CounterMode(nonce, plaintext, sealedBytes, Contract.TagBytes);
            var tag = Ghash(sealedBytes, Contract.TagBytes, plaintext.Length);
            for (var i = 0; i < Block; i++) sealedBytes[i] = (byte)(tag[i] ^ tagMask[i]);
        }
        return sealedBytes;
    }

    /// <summary>
    /// Decrypts what <see cref="Seal"/> made, or null when the tag doesn't match. Lumber's plugins only encrypt; this is
    /// for the tests, which have no AesGcm on .NET Framework to check the managed build against.
    /// </summary>
    public byte[]? Open(byte[] nonce, byte[] sealedBytes)
    {
        if (sealedBytes.Length < Contract.TagBytes) return null;
        var length = sealedBytes.Length - Contract.TagBytes;
        var plaintext = new byte[length];
        lock (gate)
        {
            var tag = Ghash(sealedBytes, Contract.TagBytes, length);
            var ciphertext = new byte[length];
            Buffer.BlockCopy(sealedBytes, Contract.TagBytes, ciphertext, 0, length);
            var tagMask = CounterMode(nonce, ciphertext, plaintext, 0);
            var difference = 0;
            for (var i = 0; i < Block; i++) difference |= tag[i] ^ tagMask[i] ^ sealedBytes[i];
            if (difference != 0) return null;
        }
        return plaintext;
    }

    public override void Dispose()
    {
        encryptor.Dispose();
        aes.Dispose();
    }

    // XORs input with the keystream from counter block 2 into output at offset, and returns E(K, J0), the tag's mask
    private byte[] CounterMode(byte[] nonce, byte[] input, byte[] output, int offset)
    {
        if (nonce.Length != Contract.NonceBytes) throw new ArgumentException("AES-GCM here takes a 12-byte nonce", nameof(nonce));
        var blocks = (input.Length + Block - 1) / Block;
        var counters = new byte[(blocks + 1) * Block];
        for (var i = 0; i <= blocks; i++)
        {
            var start = i * Block;
            Buffer.BlockCopy(nonce, 0, counters, start, Contract.NonceBytes);
            var counter = (uint)(i + 1);
            counters[start + 12] = (byte)(counter >> 24);
            counters[start + 13] = (byte)(counter >> 16);
            counters[start + 14] = (byte)(counter >> 8);
            counters[start + 15] = (byte)counter;
        }
        var keystream = new byte[counters.Length];
        encryptor.TransformBlock(counters, 0, counters.Length, keystream, 0);
        for (var i = 0; i < input.Length; i++) output[offset + i] = (byte)(input[i] ^ keystream[Block + i]);
        var tagMask = new byte[Block];
        Buffer.BlockCopy(keystream, 0, tagMask, 0, Block);
        return tagMask;
    }

    // GHASH over the ciphertext (no associated data), then the lengths block
    private byte[] Ghash(byte[] data, int offset, int length)
    {
        ulong high = 0, low = 0;
        var block = new byte[Block];
        for (var start = 0; start < length; start += Block)
        {
            Array.Clear(block, 0, Block);
            Buffer.BlockCopy(data, offset + start, block, 0, Math.Min(Block, length - start));
            Multiply(ref high, ref low, ReadUInt64(block, 0), ReadUInt64(block, 8));
        }
        Multiply(ref high, ref low, 0, (ulong)length * 8);
        var result = new byte[Block];
        WriteUInt64(result, 0, high);
        WriteUInt64(result, 8, low);
        return result;
    }

    // Y = (Y xor X) * H in GF(2^128), GCM's bit order (the first bit is the highest)
    private void Multiply(ref ulong high, ref ulong low, ulong blockHigh, ulong blockLow)
    {
        var xHigh = high ^ blockHigh;
        var xLow = low ^ blockLow;
        ulong zHigh = 0, zLow = 0;
        ulong vHigh = hashKeyHigh, vLow = hashKeyLow;
        for (var i = 0; i < 128; i++)
        {
            var bit = i < 64 ? (xHigh >> (63 - i)) & 1 : (xLow >> (127 - i)) & 1;
            var take = 0UL - bit;
            zHigh ^= vHigh & take;
            zLow ^= vLow & take;
            var carry = 0UL - (vLow & 1);
            vLow = (vLow >> 1) | (vHigh << 63);
            vHigh = (vHigh >> 1) ^ (0xE100000000000000UL & carry);
        }
        high = zHigh;
        low = zLow;
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++) value = (value << 8) | bytes[offset + i];
        return value;
    }

    private static void WriteUInt64(byte[] bytes, int offset, ulong value)
    {
        for (var i = 7; i >= 0; i--)
        {
            bytes[offset + i] = (byte)value;
            value >>= 8;
        }
    }
}
