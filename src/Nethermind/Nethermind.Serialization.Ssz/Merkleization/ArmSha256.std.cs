// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;

namespace Nethermind.Serialization.Ssz.Merkleization;

/// <summary>Hashes short messages and 64-byte SSZ parents using the Arm SHA-256 instructions.</summary>
internal static class ArmSha256
{
    private static Vector128<uint> InitialAbcd => Vector128.Create(0x6a09e667u, 0xbb67ae85u, 0x3c6ef372u, 0xa54ff53au);
    private static Vector128<uint> InitialEfgh => Vector128.Create(0x510e527fu, 0x9b05688cu, 0x1f83d9abu, 0x5be0cd19u);

    private static ReadOnlySpan<uint> Constants =>
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

    // W[t] + K[t] for the second block. Every 64-byte parent has the same SHA-256 padding.
    private static ReadOnlySpan<uint> PaddingRounds =>
    [
        0xc28a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf374,
        0x649b69c1, 0xf0fe4786, 0x0fe1edc6, 0x240cf254, 0x4fe9346f, 0x6cc984be, 0x61b9411e, 0x16f988fa,
        0xf2c65152, 0xa88e5a6d, 0xb019fc65, 0xb9d99ec7, 0x9a1231c3, 0xe70eeaa0, 0xfdb1232b, 0xc7353eb0,
        0x3069bad5, 0xcb976d5f, 0x5a0f118f, 0xdc1eeefd, 0x0a35b689, 0xde0b7a04, 0x58f4ca9d, 0xe15d5b16,
        0x007f3e86, 0x37088980, 0xa507ea32, 0x6fab9537, 0x17406110, 0x0d8cd6f1, 0xcdaa3b6d, 0xc0bbbe37,
        0x83613bda, 0xdb48a363, 0x0b02e931, 0x6fd15ca7, 0x521afaca, 0x31338431, 0x6ed41a95, 0x6d437890,
        0xc39c91f2, 0x9eccabbd, 0xb5c9a0e6, 0x532fb63c, 0xd2c741c6, 0x07237ea3, 0xa4954b68, 0x4c191d76,
    ];

    internal static bool TryHashData(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < SHA256.HashSizeInBytes)
            return false;

        ref byte output = ref MemoryMarshal.GetReference(destination);
        if (source.Length == 64)
            HashOne(ref MemoryMarshal.GetReference(source), ref output);
        else if (source.Length <= 55)
            HashShort(source, ref output);
        else
            return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static State CompressFirstBlock(ref byte input)
    {
        Schedule schedule = new(ref input);
        State state = new(InitialAbcd, InitialEfgh);
        ref uint constants = ref MemoryMarshal.GetReference(Constants);
        for (nuint i = 0; i < 48; i += 4)
        {
            Vector128<uint> k = Vector128.LoadUnsafe(ref constants, i);
            state.Round(schedule.Next() + k);
        }

        state.Round(schedule.W0 + Vector128.LoadUnsafe(ref constants, 48));
        state.Round(schedule.W1 + Vector128.LoadUnsafe(ref constants, 52));
        state.Round(schedule.W2 + Vector128.LoadUnsafe(ref constants, 56));
        state.Round(schedule.W3 + Vector128.LoadUnsafe(ref constants, 60));
        return state;
    }

    [SkipLocalsInit]
    private static void HashShort(ReadOnlySpan<byte> input, ref byte output)
    {
        Span<byte> block = stackalloc byte[64];
        block.Clear();
        input.CopyTo(block);
        block[input.Length] = 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(block[62..], (ushort)(input.Length * 8));

        State state = CompressFirstBlock(ref MemoryMarshal.GetReference(block));
        state.WriteDigest(new(InitialAbcd, InitialEfgh), ref output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HashOne(ref byte input, ref byte output)
    {
        State state = CompressFirstBlock(ref input);
        state.Abcd += InitialAbcd;
        state.Efgh += InitialEfgh;
        State previous = state;
        ref uint padding = ref MemoryMarshal.GetReference(PaddingRounds);
        for (nuint i = 0; i < 64; i += 4)
        {
            state.Round(Vector128.LoadUnsafe(ref padding, i));
        }

        state.WriteDigest(previous, ref output);
    }

    private struct State(Vector128<uint> abcd, Vector128<uint> efgh)
    {
        public Vector128<uint> Abcd = abcd;
        public Vector128<uint> Efgh = efgh;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Round(Vector128<uint> wk)
        {
            Vector128<uint> old = Abcd;
            Abcd = Sha256.HashUpdate1(old, Efgh, wk);
            Efgh = Sha256.HashUpdate2(Efgh, old, wk);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void WriteDigest(State previous, ref byte output)
        {
            AdvSimd.ReverseElement8(Abcd + previous.Abcd).AsByte().StoreUnsafe(ref output);
            AdvSimd.ReverseElement8(Efgh + previous.Efgh).AsByte().StoreUnsafe(ref output, 16);
        }
    }

    private struct Schedule(ref byte input)
    {
        public Vector128<uint> W0 = Read(ref input, 0);
        public Vector128<uint> W1 = Read(ref input, 16);
        public Vector128<uint> W2 = Read(ref input, 32);
        public Vector128<uint> W3 = Read(ref input, 48);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<uint> Read(ref byte input, nuint offset) =>
            AdvSimd.ReverseElement8(Vector128.LoadUnsafe(ref input, offset).AsUInt32());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<uint> Next()
        {
            Vector128<uint> result = W0;
            Vector128<uint> next = Sha256.ScheduleUpdate1(Sha256.ScheduleUpdate0(W0, W1), W2, W3);
            W0 = W1;
            W1 = W2;
            W2 = W3;
            W3 = next;
            return result;
        }
    }
}
