// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Crypto;

/// <summary>
/// The SP1 guest's merkle pair hash, with SP1's extend and compress precompiles replaced by the executor's own
/// definitions, against the BCL's SHA-256.
/// </summary>
public class GuestSp1Sha256Tests
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
    public void Padding_schedule_is_the_extension_of_the_padding_block()
    {
        ulong[] schedule = new ulong[Merkle.Sp1Sha256ScheduleLength];
        Merkle.Sp1Sha256PaddingSchedule[..16].CopyTo(schedule);

        Extend(schedule);

        Assert.That(schedule, Is.EqualTo(Merkle.Sp1Sha256PaddingSchedule.ToArray()));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(0x5a)]
    public unsafe void Pair_hash_matches_sha256(int seed)
    {
        byte[] message = new byte[64];
        if (seed != 0)
            new Random(seed).NextBytes(message);
        UInt256 left = MemoryMarshal.Read<UInt256>(message);
        UInt256 right = MemoryMarshal.Read<UInt256>(message.AsSpan(32));

        ulong[] schedule = new ulong[Merkle.Sp1Sha256ScheduleLength];
        ulong[] state = new ulong[Merkle.Sp1Sha256StateLength];
        UInt256 digest;
        fixed (ulong* w = schedule, h = state)
        {
            Merkle.LoadSp1Sha256Block(in left, in right, w, h);
            Extend(schedule);
            Compress(schedule, state);
            Compress(Merkle.Sp1Sha256PaddingSchedule.ToArray(), state);
            digest = Merkle.ReadSp1Sha256Digest(h);
        }

        byte[] actual = new byte[32];
        MemoryMarshal.Write(actual, in digest);
        Assert.That(actual, Is.EqualTo(SHA256.HashData(message)));
    }

    // SP1's SHA_EXTEND and SHA_COMPRESS (sp1 crates/core/executor, minimal/precompiles/sha256).
    private static void Extend(ulong[] w)
    {
        for (int i = 16; i < 64; i++)
        {
            uint w15 = (uint)w[i - 15], w2 = (uint)w[i - 2];
            uint s0 = BitOperations.RotateRight(w15, 7) ^ BitOperations.RotateRight(w15, 18) ^ (w15 >> 3);
            uint s1 = BitOperations.RotateRight(w2, 17) ^ BitOperations.RotateRight(w2, 19) ^ (w2 >> 10);
            w[i] = s1 + (uint)w[i - 16] + s0 + (uint)w[i - 7];
        }
    }

    private static void Compress(ulong[] w, ulong[] state)
    {
        uint a = (uint)state[0], b = (uint)state[1], c = (uint)state[2], d = (uint)state[3];
        uint e = (uint)state[4], f = (uint)state[5], g = (uint)state[6], h = (uint)state[7];
        for (int i = 0; i < 64; i++)
        {
            uint s1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
            uint temp1 = h + s1 + ((e & f) ^ (~e & g)) + RoundConstants[i] + (uint)w[i];
            uint s0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
            uint temp2 = s0 + ((a & b) ^ (a & c) ^ (b & c));
            h = g; g = f; f = e; e = d + temp1; d = c; c = b; b = a; a = temp1 + temp2;
        }

        state[0] = (uint)state[0] + a; state[1] = (uint)state[1] + b; state[2] = (uint)state[2] + c; state[3] = (uint)state[3] + d;
        state[4] = (uint)state[4] + e; state[5] = (uint)state[5] + f; state[6] = (uint)state[6] + g; state[7] = (uint)state[7] + h;
    }
}
