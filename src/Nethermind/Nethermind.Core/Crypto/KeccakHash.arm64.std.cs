// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace Nethermind.Core.Crypto;

public sealed partial class KeccakHash
{
    [InlineArray(HASH_DATA_AREA / sizeof(ulong))]
    private struct Keccak256RateBlock
    {
        private ulong _lane0;
    }

    [SkipLocalsInit]
    private static void ComputeHash256ArmSha3(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int tailLength = input.Length % HASH_DATA_AREA;
        int tailOffset = input.Length - tailLength;
        Keccak256RateBlock padded = default;
        Span<byte> tail = MemoryMarshal.AsBytes((Span<ulong>)padded);
        input[tailOffset..].CopyTo(tail);
        tail[tailLength] = 0x01;
        tail[^1] |= 0x80;

        ref ulong source = ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(input));
        ref ulong firstBlock = ref (tailOffset == 0 ? ref padded[0] : ref source);
        ref ulong destination = ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(output));
        PermuteArmSha3<OnFlag>(ref firstBlock, ref destination, tailOffset, ref padded[0]);
    }

    /// <summary>Permutes the 25 lanes starting at <paramref name="state"/> in place.</summary>
    internal static void KeccakF1600ArmSha3(ref ulong state) =>
        PermuteArmSha3<OffFlag>(ref state, ref state, 0, ref Unsafe.NullRef<ulong>());

    // In sponge mode the first 17 lanes come from the message, the capacity starts at zero,
    // and only four digest lanes are written. A standalone permutation reads and writes all 25.
    [SkipLocalsInit]
    private static void PermuteArmSha3<TAbsorbMessage>(ref ulong state, ref ulong destination, int paddedBlockOffset, ref ulong padded)
        where TAbsorbMessage : struct, IFlag
    {
        Debug.Assert(Sha3.IsSupported);
        // Resolve the static table before loading the lanes. A class-init helper with the lanes
        // live makes the ARM64 JIT spill them throughout the round loop.
        ref ulong constants = ref MemoryMarshal.GetArrayDataReference(RoundConstants);

        // Each SHA3 instruction operates independently on both halves. Only the low halves hold the state.
        Vector128<ulong> a0 = LoadLane(ref state, 0);
        Vector128<ulong> a1 = LoadLane(ref state, 1);
        Vector128<ulong> a2 = LoadLane(ref state, 2);
        Vector128<ulong> a3 = LoadLane(ref state, 3);
        Vector128<ulong> a4 = LoadLane(ref state, 4);
        Vector128<ulong> a5 = LoadLane(ref state, 5);
        Vector128<ulong> a6 = LoadLane(ref state, 6);
        Vector128<ulong> a7 = LoadLane(ref state, 7);
        Vector128<ulong> a8 = LoadLane(ref state, 8);
        Vector128<ulong> a9 = LoadLane(ref state, 9);
        Vector128<ulong> a10 = LoadLane(ref state, 10);
        Vector128<ulong> a11 = LoadLane(ref state, 11);
        Vector128<ulong> a12 = LoadLane(ref state, 12);
        Vector128<ulong> a13 = LoadLane(ref state, 13);
        Vector128<ulong> a14 = LoadLane(ref state, 14);
        Vector128<ulong> a15 = LoadLane(ref state, 15);
        Vector128<ulong> a16 = LoadLane(ref state, 16);
        Vector128<ulong> a17 = TAbsorbMessage.IsActive ? Vector128<ulong>.Zero : LoadLane(ref state, 17);
        Vector128<ulong> a18 = TAbsorbMessage.IsActive ? Vector128<ulong>.Zero : LoadLane(ref state, 18);
        Vector128<ulong> a19 = TAbsorbMessage.IsActive ? Vector128<ulong>.Zero : LoadLane(ref state, 19);
        Vector128<ulong> a20 = TAbsorbMessage.IsActive ? Vector128<ulong>.Zero : LoadLane(ref state, 20);
        Vector128<ulong> a21 = TAbsorbMessage.IsActive ? Vector128<ulong>.Zero : LoadLane(ref state, 21);
        Vector128<ulong> a22 = TAbsorbMessage.IsActive ? Vector128<ulong>.Zero : LoadLane(ref state, 22);
        Vector128<ulong> a23 = TAbsorbMessage.IsActive ? Vector128<ulong>.Zero : LoadLane(ref state, 23);
        Vector128<ulong> a24 = TAbsorbMessage.IsActive ? Vector128<ulong>.Zero : LoadLane(ref state, 24);

        int offset = 0;
        while (true)
        {
            for (nuint round = 0; round < ROUNDS; round++)
            {
                RoundArmSha3(
                    ref a0, ref a1, ref a2, ref a3, ref a4,
                    ref a5, ref a6, ref a7, ref a8, ref a9,
                    ref a10, ref a11, ref a12, ref a13, ref a14,
                    ref a15, ref a16, ref a17, ref a18, ref a19,
                    ref a20, ref a21, ref a22, ref a23, ref a24,
                    LoadLane(ref constants, round));
            }

            if (!TAbsorbMessage.IsActive || offset == paddedBlockOffset)
                break;

            offset += HASH_DATA_AREA;
            ref ulong block = ref (offset == paddedBlockOffset ? ref padded : ref Unsafe.AddByteOffset(ref state, (nuint)offset));
            a0 ^= LoadLane(ref block, 0);
            a1 ^= LoadLane(ref block, 1);
            a2 ^= LoadLane(ref block, 2);
            a3 ^= LoadLane(ref block, 3);
            a4 ^= LoadLane(ref block, 4);
            a5 ^= LoadLane(ref block, 5);
            a6 ^= LoadLane(ref block, 6);
            a7 ^= LoadLane(ref block, 7);
            a8 ^= LoadLane(ref block, 8);
            a9 ^= LoadLane(ref block, 9);
            a10 ^= LoadLane(ref block, 10);
            a11 ^= LoadLane(ref block, 11);
            a12 ^= LoadLane(ref block, 12);
            a13 ^= LoadLane(ref block, 13);
            a14 ^= LoadLane(ref block, 14);
            a15 ^= LoadLane(ref block, 15);
            a16 ^= LoadLane(ref block, 16);
        }

        a0.GetLower().StoreUnsafe(ref destination);
        a1.GetLower().StoreUnsafe(ref destination, 1);
        a2.GetLower().StoreUnsafe(ref destination, 2);
        a3.GetLower().StoreUnsafe(ref destination, 3);
        if (TAbsorbMessage.IsActive)
            return;

        a4.GetLower().StoreUnsafe(ref destination, 4);
        a5.GetLower().StoreUnsafe(ref destination, 5);
        a6.GetLower().StoreUnsafe(ref destination, 6);
        a7.GetLower().StoreUnsafe(ref destination, 7);
        a8.GetLower().StoreUnsafe(ref destination, 8);
        a9.GetLower().StoreUnsafe(ref destination, 9);
        a10.GetLower().StoreUnsafe(ref destination, 10);
        a11.GetLower().StoreUnsafe(ref destination, 11);
        a12.GetLower().StoreUnsafe(ref destination, 12);
        a13.GetLower().StoreUnsafe(ref destination, 13);
        a14.GetLower().StoreUnsafe(ref destination, 14);
        a15.GetLower().StoreUnsafe(ref destination, 15);
        a16.GetLower().StoreUnsafe(ref destination, 16);
        a17.GetLower().StoreUnsafe(ref destination, 17);
        a18.GetLower().StoreUnsafe(ref destination, 18);
        a19.GetLower().StoreUnsafe(ref destination, 19);
        a20.GetLower().StoreUnsafe(ref destination, 20);
        a21.GetLower().StoreUnsafe(ref destination, 21);
        a22.GetLower().StoreUnsafe(ref destination, 22);
        a23.GetLower().StoreUnsafe(ref destination, 23);
        a24.GetLower().StoreUnsafe(ref destination, 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> LoadLane(ref ulong source, nuint lane) =>
        Vector64.LoadUnsafe(ref source, lane).ToVector128Unsafe();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RoundArmSha3(
        ref Vector128<ulong> a0, ref Vector128<ulong> a1, ref Vector128<ulong> a2, ref Vector128<ulong> a3, ref Vector128<ulong> a4,
        ref Vector128<ulong> a5, ref Vector128<ulong> a6, ref Vector128<ulong> a7, ref Vector128<ulong> a8, ref Vector128<ulong> a9,
        ref Vector128<ulong> a10, ref Vector128<ulong> a11, ref Vector128<ulong> a12, ref Vector128<ulong> a13, ref Vector128<ulong> a14,
        ref Vector128<ulong> a15, ref Vector128<ulong> a16, ref Vector128<ulong> a17, ref Vector128<ulong> a18, ref Vector128<ulong> a19,
        ref Vector128<ulong> a20, ref Vector128<ulong> a21, ref Vector128<ulong> a22, ref Vector128<ulong> a23, ref Vector128<ulong> a24,
        Vector128<ulong> roundConstant)
    {
        // Theta column parities and corrections (FIPS 202, section 3.2.1).
        Vector128<ulong> c0 = Sha3.Xor(Sha3.Xor(a0, a5, a10), a15, a20);
        Vector128<ulong> c1 = Sha3.Xor(Sha3.Xor(a1, a6, a11), a16, a21);
        Vector128<ulong> c2 = Sha3.Xor(Sha3.Xor(a2, a7, a12), a17, a22);
        Vector128<ulong> c3 = Sha3.Xor(Sha3.Xor(a3, a8, a13), a18, a23);
        Vector128<ulong> c4 = Sha3.Xor(Sha3.Xor(a4, a9, a14), a19, a24);
        Vector128<ulong> d0 = Sha3.BitwiseRotateLeftBy1AndXor(c4, c1);
        Vector128<ulong> d1 = Sha3.BitwiseRotateLeftBy1AndXor(c0, c2);
        Vector128<ulong> d2 = Sha3.BitwiseRotateLeftBy1AndXor(c1, c3);
        Vector128<ulong> d3 = Sha3.BitwiseRotateLeftBy1AndXor(c2, c4);
        Vector128<ulong> d4 = Sha3.BitwiseRotateLeftBy1AndXor(c3, c0);

        // XAR folds the theta correction into rho. Walk pi's 24-lane cycle backwards so each
        // assignment reads its source before it is overwritten. Lane zero is the fixed point.
        a0 ^= d0;
        Vector128<ulong> saved = a1;
        a1 = Sha3.XorRotateRight(a6, d1, 20);
        a6 = Sha3.XorRotateRight(a9, d4, 44);
        a9 = Sha3.XorRotateRight(a22, d2, 3);
        a22 = Sha3.XorRotateRight(a14, d4, 25);
        a14 = Sha3.XorRotateRight(a20, d0, 46);
        a20 = Sha3.XorRotateRight(a2, d2, 2);
        a2 = Sha3.XorRotateRight(a12, d2, 21);
        a12 = Sha3.XorRotateRight(a13, d3, 39);
        a13 = Sha3.XorRotateRight(a19, d4, 56);
        a19 = Sha3.XorRotateRight(a23, d3, 8);
        a23 = Sha3.XorRotateRight(a15, d0, 23);
        a15 = Sha3.XorRotateRight(a4, d4, 37);
        a4 = Sha3.XorRotateRight(a24, d4, 50);
        a24 = Sha3.XorRotateRight(a21, d1, 62);
        a21 = Sha3.XorRotateRight(a8, d3, 9);
        a8 = Sha3.XorRotateRight(a16, d1, 19);
        a16 = Sha3.XorRotateRight(a5, d0, 28);
        a5 = Sha3.XorRotateRight(a3, d3, 36);
        a3 = Sha3.XorRotateRight(a18, d3, 43);
        a18 = Sha3.XorRotateRight(a17, d2, 49);
        a17 = Sha3.XorRotateRight(a11, d1, 54);
        a11 = Sha3.XorRotateRight(a7, d2, 58);
        a7 = Sha3.XorRotateRight(a10, d0, 61);
        a10 = Sha3.XorRotateRight(saved, d1, 63);

        ChiRowArmSha3(ref a0, ref a1, ref a2, ref a3, ref a4);
        ChiRowArmSha3(ref a5, ref a6, ref a7, ref a8, ref a9);
        ChiRowArmSha3(ref a10, ref a11, ref a12, ref a13, ref a14);
        ChiRowArmSha3(ref a15, ref a16, ref a17, ref a18, ref a19);
        ChiRowArmSha3(ref a20, ref a21, ref a22, ref a23, ref a24);
        a0 ^= roundConstant;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ChiRowArmSha3(ref Vector128<ulong> a, ref Vector128<ulong> b, ref Vector128<ulong> c,
        ref Vector128<ulong> d, ref Vector128<ulong> e)
    {
        Vector128<ulong> first = a;
        Vector128<ulong> second = b;
        a = Sha3.BitwiseClearXor(a, c, b);
        b = Sha3.BitwiseClearXor(b, d, c);
        c = Sha3.BitwiseClearXor(c, e, d);
        d = Sha3.BitwiseClearXor(d, first, e);
        e = Sha3.BitwiseClearXor(e, second, first);
    }
}
