// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Crypto;

/// <summary>Incremental unkeyed BLAKE2s-256 (RFC 7693), the EIP-8288 <c>DEPS_HASH</c>.</summary>
internal struct Blake2s
{
    private const int BlockLength = 64;

    [InlineArray(8)]
    private struct State { private uint _e0; }

    [InlineArray(BlockLength)]
    private struct Block { private byte _e0; }

    private static ReadOnlySpan<uint> IV =>
        [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];

    private static ReadOnlySpan<byte> Sigma =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];

    private State _h;
    private Block _buffer;
    private int _buffered;
    private ulong _counter;

    public static Blake2s Create()
    {
        Blake2s hasher = default;
        IV.CopyTo(hasher._h);
        // Parameter block: 32-byte digest, no key, fanout and depth 1.
        hasher._h[0] ^= 0x01010020;
        return hasher;
    }

    public static ValueHash256 Compute(ReadOnlySpan<byte> data)
    {
        Blake2s hasher = Create();
        hasher.Update(data);
        return hasher.Finish();
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            // The final block is compressed with its flag set, so a full buffer waits for more input.
            if (_buffered == BlockLength)
            {
                _counter += BlockLength;
                Compress(final: false);
                _buffered = 0;
            }

            int take = Math.Min(BlockLength - _buffered, data.Length);
            data[..take].CopyTo(((Span<byte>)_buffer)[_buffered..]);
            _buffered += take;
            data = data[take..];
        }
    }

    public ValueHash256 Finish()
    {
        _counter += (ulong)_buffered;
        ((Span<byte>)_buffer)[_buffered..].Clear();
        Compress(final: true);
        Span<byte> digest = stackalloc byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(digest[(4 * i)..], _h[i]);
        return new ValueHash256(digest);
    }

    private void Compress(bool final)
    {
        Span<uint> m = stackalloc uint[16];
        ReadOnlySpan<byte> block = _buffer;
        for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(4 * i)..]);

        Span<uint> v = stackalloc uint[16];
        ((ReadOnlySpan<uint>)_h).CopyTo(v);
        IV.CopyTo(v[8..]);
        v[12] ^= (uint)_counter;
        v[13] ^= (uint)(_counter >> 32);
        if (final) v[14] = ~v[14];

        for (int round = 0; round < 10; round++)
        {
            ReadOnlySpan<byte> s = Sigma.Slice(16 * round, 16);
            Mix(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            Mix(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            Mix(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            Mix(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            Mix(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            Mix(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            Mix(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            Mix(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }

        for (int i = 0; i < 8; i++) _h[i] ^= v[i] ^ v[i + 8];
    }

    private static void Mix(Span<uint> v, int a, int b, int c, int d, uint x, uint y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
    }
}
