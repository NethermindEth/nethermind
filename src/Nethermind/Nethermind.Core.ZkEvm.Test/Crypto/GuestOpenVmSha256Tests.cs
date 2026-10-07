// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Serialization.Ssz.Merkleization;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Crypto;

/// <summary>
/// The OpenVM guest's merkle pair hash, with OpenVM's SHA-256 compression replaced by its definition, against the
/// BCL's SHA-256.
/// </summary>
public class GuestOpenVmSha256Tests
{
    private static readonly uint[] RoundConstants =
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ];

    [Test]
    public void Pair_hash_matches_sha256([Values(0, 1, 0x5a)] int seed)
    {
        byte[] message = new byte[64];
        if (seed != 0)
            new Random(seed).NextBytes(message);

        ulong[] state = Merkle.Sha256InitialState.ToArray();
        Compress(state, message);
        Compress(state, MemoryMarshal.AsBytes(Merkle.Sha256PaddingOf64ByteMessage));

        byte[] digest = new byte[32];
        for (int i = 0; i < state.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(i * sizeof(ulong)), Merkle.SwapBytesInHalves(state[i]));

        Assert.That(digest, Is.EqualTo(SHA256.HashData(message)));
    }

    [Test]
    public void Swapping_bytes_in_halves_matches_reversing_and_rotating([Values(0UL, 0x0123456789abcdefUL, ulong.MaxValue, 0x8000000000000001UL)] ulong value) =>
        Assert.That(Merkle.SwapBytesInHalves(value), Is.EqualTo(BitOperations.RotateRight(BinaryPrimitives.ReverseEndianness(value), 32)));

    // OpenVM's SHA-256 compression (openvm sha2 guest, zkvm_sha256_impl): the state as eight 32-bit words in
    // little-endian order, the block as FIPS 180-4 reads it.
    private static void Compress(ulong[] packedState, ReadOnlySpan<byte> block)
    {
        Span<uint> state = MemoryMarshal.Cast<ulong, uint>(packedState.AsSpan());
        uint[] w = new uint[64];
        for (int i = 0; i < 16; i++)
            w[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * sizeof(uint))..]);
        for (int i = 16; i < 64; i++)
        {
            uint s0 = BitOperations.RotateRight(w[i - 15], 7) ^ BitOperations.RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint s1 = BitOperations.RotateRight(w[i - 2], 17) ^ BitOperations.RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = 0; i < 64; i++)
        {
            uint s1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
            uint temp1 = h + s1 + ((e & f) ^ (~e & g)) + RoundConstants[i] + w[i];
            uint s0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
            uint temp2 = s0 + ((a & b) ^ (a & c) ^ (b & c));
            h = g; g = f; f = e; e = d + temp1; d = c; c = b; b = a; a = temp1 + temp2;
        }

        state[0] += a; state[1] += b; state[2] += c; state[3] += d;
        state[4] += e; state[5] += f; state[6] += g; state[7] += h;
    }
}
