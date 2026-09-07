// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto;

/// <summary>
/// This is a minimalistic one-way set associative cache for Keccak values.
///
/// It allocates only 64MB of memory to store 512k of entries.
/// No misaligned reads. Everything is aligned to both cache lines as well as to boundaries so no torn reads.
/// Uses seqlock pattern for lock-free reads: read sequence, speculatively read data, verify sequence unchanged.
/// Requires a single CAS to lock on writes and <see cref="Volatile.Write(ref int,int)"/> to unlock.
/// On a lock failure, it just moves on with execution.
/// </summary>
public static unsafe class KeccakCache
{
    /// <summary>
    /// Count is defined as a +1 over bucket mask. In the future, just change the mask as the main parameter.
    /// </summary>
    public const nuint Count = BucketMask + 1;

    private const int BucketMask = 0x0007_FFFF;
    private const uint HashMask = unchecked((uint)~BucketMask);
    private const uint LockMarker = 0x0000_8000;
    private const uint VersionMask = 0x0000_7F80;       // Bits 7-14: 8-bit version counter (0-255)
    private const uint VersionIncrement = 0x0000_0080;  // Bit 7: increment step for version
    private const uint HashLengthMask = HashMask | 0x7F;  // Hash (bits 17-31) + Length (bits 0-6)

    private const int InputLengthOfKeccak = ValueHash256.MemorySize;
    private const int InputLengthOfAddress = Address.Size;
    private const int CacheLineSizeBytes = 64;

#if ZK_EVM
    // Direct-mapped memo for the zkEVM guest. A keccak permutation is a precompile costing 38,454
    // prover units against ~16 for an aligned word read, and 47% of the inputs reaching here in one
    // mainnet block repeat: log addresses and topics, account addresses, low storage-slot indices and
    // the keccak(key || slot) of a mapping access. The guest runs one block on one thread, so the
    // seqlock the host form needs is not required, and one slot per key is enough — a collision just
    // recomputes. Only inputs of 8..64 bytes are memoized; longer ones repeat rarely and would cost
    // more to store and compare than the hash they save.
    private const nuint MinMemoLength = sizeof(ulong);
    private const nuint MaxMemoLength = 64;
    private const int MemoSlotBits = 15;
    private const int MemoSlotCount = 1 << MemoSlotBits;

    /// <summary>Knuth's multiplicative hash constant, 2^32 / phi rounded to an odd integer.</summary>
    private const uint MemoSlotMultiplier = 2654435761;

    // One slot is sixteen words so its address is a shift: the input zero-padded to whole words,
    // then the keccak, then the input length (zero while the slot is empty). The length has to be
    // part of the key — a 20-byte address and a 32-byte topic ending in twelve zero bytes pad to the
    // same words, and a contract picks its own topics.
    private const int MemoSlotShift = 4;
    private const nuint MemoValueWord = MaxMemoLength / sizeof(ulong);
    private const nuint MemoLengthWord = MemoValueWord + ValueHash256.MemorySize / sizeof(ulong);

    private static readonly ulong[] Memo = new ulong[MemoSlotCount << MemoSlotShift];
#else
    private static readonly Entry* Memory;

    static KeccakCache()
    {
        const nuint size = Count * Entry.Size;

        // Aligned, so that no torn reads if fields of Entry are properly aligned.
        Memory = (Entry*)NativeMemory.AlignedAlloc(size, BitOperations.RoundUpToPowerOf2(Entry.Size));
        NativeMemory.Clear(Memory, size);
        GC.AddMemoryPressure((long)size);
    }
#endif

    [SkipLocalsInit]
    public static ValueHash256 Compute(ReadOnlySpan<byte> input)
    {
        ComputeTo(input, out ValueHash256 keccak256);
        return keccak256;
    }

    [SkipLocalsInit]
    public static void ComputeTo(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
    {
#if ZK_EVM
        nuint length = (nuint)(uint)input.Length;
        if (length - MinMemoLength > MaxMemoLength - MinMemoLength)
        {
            keccak256 = length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(input);
            return;
        }

        ref byte inputRef = ref MemoryMarshal.GetReference(input);
        ulong tail = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, length - sizeof(ulong)));
        nuint words = length >> 3;
        nuint partial = length & 7;
        // The bytes past the last whole word are the top `partial` bytes of `tail`; shifting them down
        // gives a zero-padded final word, so the stored key never needs a byte-granular compare.
        ulong lastWord = partial == 0 ? 0 : tail >> (int)((MinMemoLength - partial) << 3);

        // Every word of the input feeds the slot index. Neither end alone will do: a big-endian storage
        // slot index is zeros up front, and the mapping preimage keccak(pad32(address) || pad32(slot))
        // is zeros up front *and* constant at the back, so an index taken from either end funnels every
        // key of one mapping into a single slot.
        ulong mixed = lastWord ^ length;
        for (nuint i = 0; i < words; i++)
        {
            mixed ^= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, i << 3));
        }

        uint folded = (uint)mixed ^ (uint)(mixed >> 32);
        ref ulong slot = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(Memo),
            (nuint)((folded * MemoSlotMultiplier) >> (32 - MemoSlotBits)) << MemoSlotShift);
        ref ulong slotLength = ref Unsafe.Add(ref slot, MemoLengthWord);

        if (slotLength == length)
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
                return;
            }
        }

    Miss:
        keccak256 = ValueKeccak.Compute(input);

        for (nuint i = 0; i < words; i++)
        {
            Unsafe.Add(ref slot, i) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputRef, i << 3));
        }

        if (partial != 0)
        {
            Unsafe.Add(ref slot, words) = lastWord;
        }

        Unsafe.As<ulong, ValueHash256>(ref Unsafe.Add(ref slot, MemoValueWord)) = keccak256;
        slotLength = length;
#else
        // Special cases jump forward as unpredicted
        if (input.Length is 0 or > Entry.MaxPayloadLength)
        {
            goto Uncommon;
        }

        int hashCode = input.FastHash();
        uint index = (uint)hashCode & BucketMask;

        Debug.Assert(index < Count);

        ref Entry e = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(Memory), index);
        if (Sse.IsSupported)
        {
            // This would be a GC hole if was managed memory, but it's native.
            // Regardless, prefetch is non-faulting so it's safe.
            Sse.PrefetchNonTemporal((byte*)Unsafe.AsPointer(ref e) + CacheLineSizeBytes);
        }

        // Half the hash is encoded in the bucket so we only need half of it and can use other half for length.
        // This allows to create a combined value that represents a part of the hash, the input's length and the lock marker.
        uint combined = (HashMask & (uint)hashCode) | (uint)input.Length;

        // Seqlock pattern: read sequence, speculatively read data, verify sequence unchanged.
        // This is lock-free for reads - no CAS required unless we need to write.
        uint seq1 = Volatile.Read(ref e.Combined);

        // Early exit: lock held or hash/length mismatch (ignoring version bit).
        if ((seq1 & LockMarker) == 0 && (seq1 & HashLengthMask) == combined)
        {
            // Fast path for 32-byte input - only copy the 32 bytes we need, not full 92-byte Payload
            if (input.Length == InputLengthOfKeccak)
            {
                // Speculative reads - copy only Vector256 at Aligned32 and the keccak result
                Vector256<byte> copyVec = Unsafe.As<byte, Vector256<byte>>(ref e.Value.Aligned32);
                ValueHash256 cachedKeccak = e.Keccak256;

                // ARM memory barrier: ensure speculative reads complete before seq2.
                // On x86/x64 (TSO), loads are never reordered - JIT eliminates this entirely.
                if (!Sse.IsSupported)
                    Interlocked.MemoryBarrier();

                // Re-read sequence - if changed, a write occurred during our reads
                if (seq1 == Volatile.Read(ref e.Combined) &&
                    copyVec == Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(input)))
                {
                    keccak256 = cachedKeccak;
                    return;
                }
            }
            else if (input.Length == InputLengthOfAddress)
            {
                // Speculative reads for 20-byte address - copy uint + Vector128
                uint copyStart = Unsafe.As<byte, uint>(ref e.Value.Start);
                Vector128<byte> copyAligned = Unsafe.As<byte, Vector128<byte>>(ref e.Value.Aligned32);
                ValueHash256 cachedKeccak = e.Keccak256;

                // ARM memory barrier (see above)
                if (!Sse.IsSupported)
                    Interlocked.MemoryBarrier();

                ref byte inputRef = ref MemoryMarshal.GetReference(input);
                // Re-read sequence and compare
                if (seq1 == Volatile.Read(ref e.Combined) &&
                    copyStart == Unsafe.As<byte, uint>(ref inputRef) &&
                    copyAligned == Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref inputRef, sizeof(uint))))
                {
                    keccak256 = cachedKeccak;
                    return;
                }
            }
            else
            {
                // Uncommon path: copy full Payload for other lengths
                Payload copy = e.Value;
                ValueHash256 cachedKeccak = e.Keccak256;

                // ARM memory barrier (see above)
                if (!Sse.IsSupported)
                    Interlocked.MemoryBarrier();

                if (seq1 == Volatile.Read(ref e.Combined) &&
                    MemoryMarshal.CreateReadOnlySpan(ref copy.Start, input.Length).SequenceEqual(input))
                {
                    keccak256 = cachedKeccak;
                    return;
                }
            }
        }

        keccak256 = ValueKeccak.Compute(input);

        uint existing = Volatile.Read(ref e.Combined);

        // Increment 8-bit version counter (wraps after 256) to detect ABA in seqlock readers.
        // 256 writes needed to wrap = ~8-13μs, far exceeding ~50ns read window.
        uint newVersion = ((existing & VersionMask) + VersionIncrement) & VersionMask;
        uint toStore = combined | newVersion;

        // Try to set to the combined locked state, if not already locked.
        if ((existing & LockMarker) == 0 && Interlocked.CompareExchange(ref e.Combined, toStore | LockMarker, existing) == existing)
        {
            e.Keccak256 = keccak256;

            // Fast copy for 2 common sizes
            if (input.Length == InputLengthOfKeccak)
            {
                // UInt256 or Hash256 which is Vector256
                Unsafe.As<byte, Vector256<byte>>(ref e.Value.Aligned32) =
                    Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(input));
            }
            else if (input.Length == InputLengthOfAddress)
            {
                // Address
                ref byte bytes1 = ref MemoryMarshal.GetReference(input);
                // 20 bytes which is uint+Vector128
                Unsafe.As<byte, uint>(ref e.Value.Start) = Unsafe.As<byte, uint>(ref bytes1);
                Unsafe.As<byte, Vector128<byte>>(ref e.Value.Aligned32) =
                    Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref bytes1, sizeof(uint)));
            }
            else
            {
                // Non 32 byte or 20 byte input; call CopyTo
                input.CopyTo(MemoryMarshal.CreateSpan(ref e.Value.Start, input.Length));
            }

            // Release the lock, by setting to combined with new version (without lock).
            Volatile.Write(ref e.Combined, toStore);
        }

        return;

    Uncommon:
        keccak256 = input.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(input);
#endif
    }

    /// <summary>
    /// Gets the bucket for tests.
    /// </summary>
    public static uint GetBucket(ReadOnlySpan<byte> input) => (uint)input.FastHash() & BucketMask;

    /// <summary>
    /// An entry to cache keccak
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    private struct Entry
    {
        /// <summary>
        /// The size will make it 2 CPU cache entries.
        /// </summary>
        public const int Size = 128;

        private const int PayloadStart = sizeof(uint);
        private const int ValueStart = Size - ValueHash256.MemorySize;
        public const int MaxPayloadLength = ValueStart - PayloadStart;

        /// <summary>
        /// Represents a combined value for: hash, length and a potential <see cref="KeccakCache.LockMarker"/>.
        /// </summary>
        [FieldOffset(0)] public uint Combined;

        /// <summary>
        /// The actual value
        /// </summary>
        [FieldOffset(PayloadStart)] public Payload Value;

        /// <summary>
        /// The Keccak of the Value
        /// </summary>
        [FieldOffset(ValueStart)] public ValueHash256 Keccak256;
    }

    [StructLayout(LayoutKind.Explicit, Size = Entry.MaxPayloadLength)]
    private struct Payload
    {
        private const int AlignedStart = Entry.MaxPayloadLength - 32;

        [FieldOffset(0)] public byte Start;
        [FieldOffset(AlignedStart)] public byte Aligned32;
    }
}
