// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Nethermind.Core.Crypto;

/// <summary>
/// Managed implementation of SHA-256 based on the
/// <see href="https://github.com/microsoft/referencesource/blob/main/mscorlib/system/security/cryptography/sha256managed.cs">
/// .NET Reference Source</see> for zkVM-compatibility.
/// </summary>
public sealed class SHA256Managed : SHA256
{
    private static ReadOnlySpan<uint> K =>
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5,
        0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
        0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
        0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
        0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
        0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5,
        0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
        0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
    ];

    private readonly byte[] _buffer = new byte[64];
    private readonly uint[] _state = new uint[8];
    private readonly uint[] _w = new uint[64];
    private long _count;

    public SHA256Managed() => InitializeState();

    public override void Initialize()
    {
        InitializeState();
        _buffer.AsSpan().Clear();
        _w.AsSpan().Clear();
    }

    public static new byte[] HashData(ReadOnlySpan<byte> source)
    {
        using SHA256Managed sha256 = new();
        sha256.HashCore(source);
        return sha256.HashFinal();
    }

    protected override void HashCore(byte[] array, int start, int length) => HashCore(array.AsSpan(start, length));

    protected override void HashCore(ReadOnlySpan<byte> source)
    {
        int bufferLen = (int)(_count & 0x3f);

        _count += source.Length;

        if (bufferLen > 0 && bufferLen + source.Length >= 64)
        {
            int bytesToCopy = 64 - bufferLen;
            source[..bytesToCopy].CopyTo(_buffer.AsSpan(bufferLen));
            source = source[bytesToCopy..];
            SHATransform(_w, _state, _buffer);
            bufferLen = 0;
        }

        while (source.Length >= 64)
        {
            SHATransform(_w, _state, source[..64]);
            source = source[64..];
        }

        if (source.Length > 0)
        {
            source.CopyTo(_buffer.AsSpan(bufferLen));
        }
    }

    protected override byte[] HashFinal()
    {
        Span<byte> hash = stackalloc byte[32];
        TryHashFinal(hash, out _);
        return hash.ToArray();
    }

    protected override bool TryHashFinal(Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 32)
        {
            bytesWritten = 0;
            return false;
        }

        int padLen = 64 - (int)(_count & 0x3f);

        if (padLen <= 8)
            padLen += 64;

        Span<byte> pad = stackalloc byte[padLen];
        pad.Clear();
        pad[0] = 0x80;

        long bitCount = _count * 8;
        BinaryPrimitives.WriteInt64BigEndian(pad[(padLen - 8)..], bitCount);

        HashCore(pad);

        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[(i * 4)..], _state[i]);
        }

        bytesWritten = 32;
        return true;
    }

    private void InitializeState()
    {
        _count = 0;
        _state[0] = 0x6a09e667;
        _state[1] = 0xbb67ae85;
        _state[2] = 0x3c6ef372;
        _state[3] = 0xa54ff53a;
        _state[4] = 0x510e527f;
        _state[5] = 0x9b05688c;
        _state[6] = 0x1f83d9ab;
        _state[7] = 0x5be0cd19;
    }

    private static void SHATransform(Span<uint> w, Span<uint> state, ReadOnlySpan<byte> block)
    {
        // Load first 16 words from block (big-endian)
        for (int i = 0; i < 16; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
        }

        // Expand to 64 words
        for (int i = 16; i < 64; i++)
        {
            w[i] = Sigma1Small(w[i - 2]) + w[i - 7] + Sigma0Small(w[i - 15]) + w[i - 16];
        }

        uint a = state[0], b = state[1], c = state[2], d = state[3];
        uint e = state[4], f = state[5], g = state[6], h = state[7];

        ReadOnlySpan<uint> k = K;

        // Unrolled loop - process 8 rounds at a time for better performance
        for (int j = 0; j < 64; j += 8)
        {
            Round(ref a, b, c, ref d, e, f, g, ref h, k[j], w[j]);
            Round(ref h, a, b, ref c, d, e, f, ref g, k[j + 1], w[j + 1]);
            Round(ref g, h, a, ref b, c, d, e, ref f, k[j + 2], w[j + 2]);
            Round(ref f, g, h, ref a, b, c, d, ref e, k[j + 3], w[j + 3]);
            Round(ref e, f, g, ref h, a, b, c, ref d, k[j + 4], w[j + 4]);
            Round(ref d, e, f, ref g, h, a, b, ref c, k[j + 5], w[j + 5]);
            Round(ref c, d, e, ref f, g, h, a, ref b, k[j + 6], w[j + 6]);
            Round(ref b, c, d, ref e, f, g, h, ref a, k[j + 7], w[j + 7]);
        }

        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
        state[4] += e;
        state[5] += f;
        state[6] += g;
        state[7] += h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Round(ref uint a, uint b, uint c, ref uint d, uint e, uint f, uint g, ref uint h, uint k, uint w)
    {
        uint t1 = h + Sigma1Big(e) + Ch(e, f, g) + k + w;
        uint t2 = Sigma0Big(a) + Maj(a, b, c);
        d += t1;
        h = t1 + t2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Ch(uint x, uint y, uint z) => (x & y) ^ (~x & z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Maj(uint x, uint y, uint z) => (x & y) ^ (x & z) ^ (y & z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sigma0Big(uint x) =>
        BitOperations.RotateRight(x, 2) ^ BitOperations.RotateRight(x, 13) ^ BitOperations.RotateRight(x, 22);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sigma1Big(uint x) =>
        BitOperations.RotateRight(x, 6) ^ BitOperations.RotateRight(x, 11) ^ BitOperations.RotateRight(x, 25);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sigma0Small(uint x) =>
        BitOperations.RotateRight(x, 7) ^ BitOperations.RotateRight(x, 18) ^ (x >> 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sigma1Small(uint x) =>
        BitOperations.RotateRight(x, 17) ^ BitOperations.RotateRight(x, 19) ^ (x >> 10);
}
