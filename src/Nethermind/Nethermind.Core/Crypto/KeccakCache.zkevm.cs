// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Crypto;

public static partial class KeccakCache
{
    // Direct-mapped memo for the zkEVM guest. A keccak permutation is a precompile costing 38,454
    // prover units against ~16 for a word read (unaligned here), and 47% of the inputs reaching here in one
    // mainnet block repeat: log addresses and topics, account addresses, low storage-slot indices and
    // the keccak(key || slot) of a mapping access. The guest runs one block on one thread, so the
    // seqlock the host form needs is not required, and one slot per key is enough — a collision just
    // recomputes. Only inputs of 8..64 bytes are memoized; longer ones repeat rarely and would cost
    // more to store and compare than the hash they save.
    internal const nuint MinMemoLength = sizeof(ulong);
    internal const nuint MaxMemoLength = 64;
    internal const int MemoSlotBits = 15;
    private const int MemoSlotCount = 1 << MemoSlotBits;

    /// <summary>Knuth's multiplicative hash constant, 2^32 / phi rounded to an odd integer.</summary>
    private const uint MemoSlotMultiplier = 2654435761;

    // One slot is sixteen words so its address is a shift: the input zero-padded to whole words,
    // then the keccak, then the input length (zero while the slot is empty). The length has to be
    // part of the key — a 20-byte address and a 32-byte topic ending in twelve zero bytes pad to the
    // same words, and a contract picks its own topics.
    private const int MemoSlotShift = 4;
    private const int MemoSlotWords = 1 << MemoSlotShift;
    private const nuint MemoValueWord = MaxMemoLength / sizeof(ulong);
    private const nuint MemoLengthWord = MemoValueWord + ValueHash256.MemorySize / sizeof(ulong);

    // Constant arithmetic is checked, so this underflows and fails the build if the padded key, the
    // digest and the length word ever stop fitting in one slot - which would silently alias the next.
    private const nuint MemoSlotHeadroom = MemoSlotWords - (MemoLengthWord + 1);

    // "Fits" is not the whole invariant: a MaxMemoLength that is not a whole number of words would put
    // a partial input's tail word at MemoValueWord, where the store lays the digest over it and the
    // probe then compares against that digest. This underflows if either constant moves off its
    // precondition - MinMemoLength below one word is what MemoLastKeyWord's offset needs.
    private const nuint MemoKeyAligned = (MinMemoLength - sizeof(ulong)) - MaxMemoLength % sizeof(ulong);

    private static readonly ulong[] Memo = new ulong[MemoSlotCount * MemoSlotWords];

    [SkipLocalsInit]
    public static void ComputeTo(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
    {
        nuint length = (nuint)(uint)input.Length;
        if (length - MinMemoLength > MaxMemoLength - MinMemoLength)
        {
            keccak256 = length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(input);
            return;
        }

        ref byte inputRef = ref MemoryMarshal.GetReference(input);
        nuint words = length >> 3;
        nuint partial = length & 7;
        ulong lastWord = MemoLastKeyWord(ref inputRef, length, partial);
        ref ulong slot = ref MemoSlot(ref inputRef, length, words, lastWord);

        if (TryReadSlot(ref slot, ref inputRef, length, words, partial, lastWord, out keccak256))
        {
            return;
        }

        keccak256 = ValueKeccak.Compute(input);
        WriteSlot(ref slot, ref inputRef, length, words, partial, lastWord, keccak256);
    }

    /// <summary>Reads the digest memoized for an input, if its slot still holds that input.</summary>
    /// <param name="input">An input of <see cref="MinMemoLength"/> to <see cref="MaxMemoLength"/> bytes.</param>
    /// <param name="keccak256">The memoized digest, or default on a miss.</param>
    /// <returns>Whether the digest was memoized.</returns>
    /// <remarks>
    /// A seam for tests, which cannot go through <see cref="ComputeTo"/> because the guest's keccak is a
    /// zkVM precompile no host process can call. Deriving the slot here rather than taking it keeps the
    /// seam off the guest's path: <see cref="ComputeTo"/> derives once and probes and stores against that.
    /// </remarks>
    internal static bool TryReadMemo(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
    {
        nuint length = (nuint)(uint)input.Length;
        Debug.Assert(length - MinMemoLength <= MaxMemoLength - MinMemoLength, "input outside the memoized length range");

        ref byte inputRef = ref MemoryMarshal.GetReference(input);
        nuint words = length >> 3;
        nuint partial = length & 7;
        ulong lastWord = MemoLastKeyWord(ref inputRef, length, partial);
        return TryReadSlot(ref MemoSlot(ref inputRef, length, words, lastWord), ref inputRef, length, words, partial, lastWord, out keccak256);
    }

    /// <summary>Memoizes a digest for an input, replacing whatever its slot held.</summary>
    /// <param name="input">An input of <see cref="MinMemoLength"/> to <see cref="MaxMemoLength"/> bytes.</param>
    /// <param name="keccak256">The digest of <paramref name="input"/>.</param>
    /// <remarks><inheritdoc cref="TryReadMemo" path="/remarks"/></remarks>
    internal static void WriteMemo(ReadOnlySpan<byte> input, in ValueHash256 keccak256)
    {
        nuint length = (nuint)(uint)input.Length;
        Debug.Assert(length - MinMemoLength <= MaxMemoLength - MinMemoLength, "input outside the memoized length range");

        ref byte inputRef = ref MemoryMarshal.GetReference(input);
        nuint words = length >> 3;
        nuint partial = length & 7;
        ulong lastWord = MemoLastKeyWord(ref inputRef, length, partial);
        WriteSlot(ref MemoSlot(ref inputRef, length, words, lastWord), ref inputRef, length, words, partial, lastWord, keccak256);
    }

    /// <summary>Reads <paramref name="slot"/>'s digest, if the slot still holds the probed input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadSlot(ref ulong slot, ref byte inputRef, nuint length, nuint words, nuint partial, ulong lastWord, out ValueHash256 keccak256)
    {
        if (Unsafe.Add(ref slot, MemoLengthWord) == length)
        {
            for (nuint i = 0; i < words; i++)
            {
                if (Unsafe.Add(ref slot, i) != Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, i << 3)))
                {
                    goto Miss;
                }
            }

            if (partial == 0 || Unsafe.Add(ref slot, words) == lastWord)
            {
                keccak256 = Unsafe.As<ulong, ValueHash256>(ref Unsafe.Add(ref slot, MemoValueWord));
                return true;
            }
        }

    Miss:
        keccak256 = default;
        return false;
    }

    /// <summary>Stores a digest and its input's key in <paramref name="slot"/>, replacing whatever it held.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSlot(ref ulong slot, ref byte inputRef, nuint length, nuint words, nuint partial, ulong lastWord, in ValueHash256 keccak256)
    {
        for (nuint i = 0; i < words; i++)
        {
            Unsafe.Add(ref slot, i) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, i << 3));
        }

        if (partial != 0)
        {
            Unsafe.Add(ref slot, words) = lastWord;
        }

        Unsafe.As<ulong, ValueHash256>(ref Unsafe.Add(ref slot, MemoValueWord)) = keccak256;
        Unsafe.Add(ref slot, MemoLengthWord) = length;
    }

    /// <summary>The bytes past the input's last whole word, zero-padded to a word.</summary>
    /// <remarks>
    /// Those bytes are the top <paramref name="partial"/> ones of the word ending the input on a
    /// little-endian target, so shifting them down zero-pads them and the stored key never needs a
    /// byte-granular compare. A big-endian target would derive a different word - harmless for
    /// correctness, since key and probe stay consistent, but it would scatter the slot index. riscv64
    /// and every host this runs on are little-endian.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MemoLastKeyWord(ref byte inputRef, nuint length, nuint partial) => partial == 0
        ? 0
        : Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, length - sizeof(ulong))) >> (int)((MinMemoLength - partial) << 3);

    /// <summary>The input's slot in <see cref="Memo"/>.</summary>
    /// <remarks>
    /// Every word of the input feeds the index. Neither end alone will do: a big-endian storage slot
    /// index is zeros up front, and the mapping preimage keccak(pad32(address) || pad32(slot)) is zeros
    /// up front *and* constant at the back, so an index taken from either end funnels every key of one
    /// mapping into a single slot.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ulong MemoSlot(ref byte inputRef, nuint length, nuint words, ulong lastWord)
    {
        ulong mixed = lastWord ^ length;
        for (nuint i = 0; i < words; i++)
        {
            mixed ^= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, i << 3));
        }

        uint folded = (uint)mixed ^ (uint)(mixed >> 32);
        return ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(Memo),
            (nuint)((folded * MemoSlotMultiplier) >> (32 - MemoSlotBits)) << MemoSlotShift);
    }
}
