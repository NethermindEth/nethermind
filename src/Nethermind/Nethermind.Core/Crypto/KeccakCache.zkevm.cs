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
    // prover units against ~16 for a word read (unaligned here), and 47% of the in-range inputs in one
    // mainnet block repeat: log addresses and topics, account addresses, low storage-slot indices and
    // the keccak(key || slot) of a mapping access. The guest runs one block on one thread, so the
    // seqlock the host form needs is not required, and one slot per key is enough — a collision just
    // recomputes. Only inputs of 8..64 bytes are memoized; longer ones repeat rarely and would cost
    // more to store and compare than the hash they save.
    internal const nuint MinMemoLength = sizeof(ulong);
    internal const nuint MaxMemoLength = 64;
    internal const int MemoSlotBits = 17;
    private const int MemoSlotCount = 1 << MemoSlotBits;

    // Underflows and fails the build off either end of MemoSlot's precondition: at 0 the multiplied hash
    // is shifted by 32 and the index is undefined, at 32 the count folds to one slot while the index
    // reaches 2^32 and the store runs off the array.
    private const nuint MemoSlotBitsInRange = ((nuint)MemoSlotBits - 1) + (31 - (nuint)MemoSlotBits);

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

    // Allocated on first use rather than by an initializer, which would give the class a static
    // constructor: its run-once check is a helper call on every probe, and a call on the hit path
    // makes the JIT keep the key words in callee-saved registers it then has to save and restore.
    private static ulong[]? _memo;

    private static ulong[] Memo => _memo ??= new ulong[MemoSlotCount * MemoSlotWords];

    /// <remarks>
    /// The three lengths most callers hash - an address, a word and a pair of words - probe through
    /// <see cref="ProbeFixed"/>, where the length is a constant and the key words stay in registers from
    /// the slot index to the compare. A miss leaves through a call in tail position, so the hit path
    /// saves no callee-saved registers.
    /// </remarks>
    [SkipLocalsInit]
    public static void ComputeTo(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
    {
        ulong[]? memo = _memo;
        if (memo is not null)
        {
            ref ulong memoRef = ref MemoryMarshal.GetArrayDataReference(memo);
            switch (input.Length)
            {
                case 32:
                    ComputeToFixed(ref memoRef, input, 32, out keccak256);
                    return;
                case 64:
                    ComputeToFixed(ref memoRef, input, 64, out keccak256);
                    return;
                case 20:
                    ComputeToFixed(ref memoRef, input, 20, out keccak256);
                    return;
            }
        }

        ComputeToAnyLength(input, out keccak256);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeToFixed(ref ulong memo, ReadOnlySpan<byte> input, nuint length, out ValueHash256 keccak256)
    {
        ref ulong slot = ref ProbeFixed(ref memo, ref MemoryMarshal.GetReference(input), length, out bool hit);
        if (hit)
        {
            keccak256 = Unsafe.As<ulong, ValueHash256>(ref Unsafe.Add(ref slot, MemoValueWord));
            return;
        }

        switch (length)
        {
            case 20:
                ComputeAndMemoize20(input, ref slot, out keccak256);
                return;
            case 32:
                ComputeAndMemoize32(input, ref slot, out keccak256);
                return;
            default:
                Debug.Assert(length == 64);
                ComputeAndMemoize64(input, ref slot, out keccak256);
                return;
        }
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ComputeAndMemoize20(ReadOnlySpan<byte> input, ref ulong slot, out ValueHash256 keccak256) =>
        ComputeAndMemoizeFixed(input, ref slot, 20, out keccak256);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ComputeAndMemoize32(ReadOnlySpan<byte> input, ref ulong slot, out ValueHash256 keccak256) =>
        ComputeAndMemoizeFixed(input, ref slot, 32, out keccak256);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ComputeAndMemoize64(ReadOnlySpan<byte> input, ref ulong slot, out ValueHash256 keccak256) =>
        ComputeAndMemoizeFixed(input, ref slot, 64, out keccak256);

    /// <summary>Hashes an input of constant <paramref name="length"/> that missed its slot and memoizes the digest there.</summary>
    /// <remarks>The slot is written before the caller's output, which may alias the input.</remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeAndMemoizeFixed(ReadOnlySpan<byte> input, ref ulong slot, nuint length, out ValueHash256 keccak256)
    {
        ValueHash256 digest = ValueKeccak.Compute(input);
        WriteSlotFixed(ref slot, ref MemoryMarshal.GetReference(input), length, digest);
        keccak256 = digest;
    }

    /// <summary>Stores a digest and its constant-length input's key in <paramref name="slot"/>, writing the
    /// key words as <see cref="ProbeFixed"/> reads them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSlotFixed(ref ulong slot, ref byte inputRef, nuint length, in ValueHash256 keccak256)
    {
        nuint keyWords = (length + 7) >> 3;
        if (keyWords > 0) Unsafe.Add(ref slot, 0) = KeyWord(ref inputRef, length, 0);
        if (keyWords > 1) Unsafe.Add(ref slot, 1) = KeyWord(ref inputRef, length, 1);
        if (keyWords > 2) Unsafe.Add(ref slot, 2) = KeyWord(ref inputRef, length, 2);
        if (keyWords > 3) Unsafe.Add(ref slot, 3) = KeyWord(ref inputRef, length, 3);
        if (keyWords > 4) Unsafe.Add(ref slot, 4) = KeyWord(ref inputRef, length, 4);
        if (keyWords > 5) Unsafe.Add(ref slot, 5) = KeyWord(ref inputRef, length, 5);
        if (keyWords > 6) Unsafe.Add(ref slot, 6) = KeyWord(ref inputRef, length, 6);
        if (keyWords > 7) Unsafe.Add(ref slot, 7) = KeyWord(ref inputRef, length, 7);
        Unsafe.As<ulong, ValueHash256>(ref Unsafe.Add(ref slot, MemoValueWord)) = keccak256;
        Unsafe.Add(ref slot, MemoLengthWord) = length;
    }

    /// <summary>Hashes an input that missed its slot and memoizes the digest there.</summary>
    /// <remarks>The slot is written before the caller's output, which may alias the input.</remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ComputeAndMemoize(ReadOnlySpan<byte> input, ref ulong slot, out ValueHash256 keccak256)
    {
        nuint length = (nuint)(uint)input.Length;
        ref byte inputRef = ref MemoryMarshal.GetReference(input);
        nuint partial = length & 7;
        ValueHash256 digest = ValueKeccak.Compute(input);
        WriteSlot(ref slot, ref inputRef, length, length >> 3, partial, MemoLastKeyWord(ref inputRef, length, partial), digest);
        keccak256 = digest;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ComputeToAnyLength(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
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

        ComputeAndMemoize(input, ref slot, out keccak256);
    }

    internal static bool TryGet(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
    {
        if ((nuint)(uint)input.Length - MinMemoLength > MaxMemoLength - MinMemoLength)
        {
            Unsafe.SkipInit(out keccak256);
            return false;
        }
        return TryReadMemo(input, out keccak256);
    }

    internal static void Store(ReadOnlySpan<byte> input, in ValueHash256 keccak256)
    {
        if ((nuint)(uint)input.Length - MinMemoLength > MaxMemoLength - MinMemoLength) return;
        WriteMemo(input, keccak256);
    }

    /// <summary>Reads the digest memoized for an input, if its slot still holds that input.</summary>
    /// <param name="input">An input of <see cref="MinMemoLength"/> to <see cref="MaxMemoLength"/> bytes.</param>
    /// <param name="keccak256">The memoized digest, or unspecified on a miss.</param>
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
        if (length is 20 or 32 or 64)
        {
            ref ulong slot = ref ProbeFixed(ref MemoryMarshal.GetArrayDataReference(Memo), ref inputRef, length, out bool hit);
            if (hit)
            {
                keccak256 = Unsafe.As<ulong, ValueHash256>(ref Unsafe.Add(ref slot, MemoValueWord));
                return true;
            }

            Unsafe.SkipInit(out keccak256);
            return false;
        }

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
        if (length is 20 or 32 or 64)
        {
            WriteSlotFixed(ref ProbeFixed(ref MemoryMarshal.GetArrayDataReference(Memo), ref inputRef, length, out _), ref inputRef, length, keccak256);
            return;
        }

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
        // Not assigned: the write would land in the caller's output before ComputeTo hashes, where the
        // host arm assigns only after hashing, so an aliased in and out would diverge by build.
        Unsafe.SkipInit(out keccak256);
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

    /// <summary>Finds a fixed-length input's slot and whether it holds that input.</summary>
    /// <param name="memo">The first word of <see cref="Memo"/>.</param>
    /// <param name="inputRef">The input's first byte.</param>
    /// <param name="length">A constant from <see cref="MinMemoLength"/> to <see cref="MaxMemoLength"/>, so every
    /// key word is a fixed-offset read and the words past the key fold away.</param>
    /// <param name="hit">Whether the slot holds the input.</param>
    /// <remarks>Derives the same slot and compares the same key as <see cref="MemoSlot"/> and
    /// <see cref="TryReadSlot"/>, reading each key word once.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ulong ProbeFixed(ref ulong memo, ref byte inputRef, nuint length, out bool hit)
    {
        ulong k0 = KeyWord(ref inputRef, length, 0);
        ulong k1 = KeyWord(ref inputRef, length, 1);
        ulong k2 = KeyWord(ref inputRef, length, 2);
        ulong k3 = KeyWord(ref inputRef, length, 3);
        ulong k4 = KeyWord(ref inputRef, length, 4);
        ulong k5 = KeyWord(ref inputRef, length, 5);
        ulong k6 = KeyWord(ref inputRef, length, 6);
        ulong k7 = KeyWord(ref inputRef, length, 7);
        ref ulong slot = ref SlotOf(ref memo, length ^ k0 ^ k1 ^ k2 ^ k3 ^ k4 ^ k5 ^ k6 ^ k7);

        nuint keyWords = (length + 7) >> 3;
        ulong difference = Unsafe.Add(ref slot, MemoLengthWord) ^ length;
        if (keyWords > 0) difference |= Unsafe.Add(ref slot, 0) ^ k0;
        if (keyWords > 1) difference |= Unsafe.Add(ref slot, 1) ^ k1;
        if (keyWords > 2) difference |= Unsafe.Add(ref slot, 2) ^ k2;
        if (keyWords > 3) difference |= Unsafe.Add(ref slot, 3) ^ k3;
        if (keyWords > 4) difference |= Unsafe.Add(ref slot, 4) ^ k4;
        if (keyWords > 5) difference |= Unsafe.Add(ref slot, 5) ^ k5;
        if (keyWords > 6) difference |= Unsafe.Add(ref slot, 6) ^ k6;
        if (keyWords > 7) difference |= Unsafe.Add(ref slot, 7) ^ k7;
        hit = difference == 0;
        return ref slot;
    }

    /// <summary>Word <paramref name="index"/> of an input's key: a whole input word, then the zero-padded
    /// partial word, then zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong KeyWord(ref byte inputRef, nuint length, nuint index) =>
        index < length >> 3 ? Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, index << 3))
        : index == length >> 3 ? MemoLastKeyWord(ref inputRef, length, length & 7)
        : 0;

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
        : Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, length - sizeof(ulong))) >> (int)((sizeof(ulong) - partial) << 3);

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

        return ref SlotOf(ref MemoryMarshal.GetArrayDataReference(Memo), mixed);
    }

    /// <summary>The slot of an input whose length and key words XOR to <paramref name="mixed"/>.</summary>
    /// <param name="memo">The first word of <see cref="Memo"/>.</param>
    /// <param name="mixed">The input's length XORed with its key words.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ulong SlotOf(ref ulong memo, ulong mixed)
    {
        ulong folded = mixed ^ (mixed >> 32);
        // The top bits of the product's low half, taken by shifts: masking it is a 32-bit multiply and a zero-extend on RV64.
        return ref Unsafe.Add(
            ref memo,
            (nuint)(((folded * MemoSlotMultiplier) << 32) >> (64 - MemoSlotBits)) << MemoSlotShift);
    }
}
