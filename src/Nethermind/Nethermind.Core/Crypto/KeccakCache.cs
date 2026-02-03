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
/// It allocates only 16MB of memory to store 128k of entries.
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
    public const int Count = BucketMask + 1;

    private const int BucketMask = 0x0001_FFFF;
    private const ulong HashMask = 0xFFFF_FFFF_FFFF_0000;  // Bits 16-63: 48-bit hash
    private const ulong LockMarker = 0x0000_0000_0000_8000;
    private const ulong VersionMask = 0x0000_0000_0000_7F80;       // Bits 7-14: 8-bit version counter (0-255)
    private const ulong VersionIncrement = 0x0000_0000_0000_0080;  // Bit 7: increment step for version
    private const ulong HashLengthMask = HashMask | 0x7F;  // Hash (bits 16-63) + Length (bits 0-6)

    private const int InputLengthOfKeccak = ValueHash256.MemorySize;
    private const int InputLengthOfAddress = Address.Size;
    private const int CacheLineSizeBytes = 64;
    private static readonly Entry* Memory;

    static KeccakCache()
    {
        const UIntPtr size = Count * Entry.Size;

        // Aligned, so that no torn reads if fields of Entry are properly aligned.
        Memory = (Entry*)NativeMemory.AlignedAlloc(size, BitOperations.RoundUpToPowerOf2(Entry.Size));
        NativeMemory.Clear(Memory, size);
        GC.AddMemoryPressure((long)size);
    }

    [SkipLocalsInit]
    public static ValueHash256 Compute(ReadOnlySpan<byte> input)
    {
        ComputeTo(input, out ValueHash256 keccak256);
        return keccak256;
    }

    [SkipLocalsInit]
    public static void ComputeTo(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
    {
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
        ulong combined = (HashMask & ((ulong)(uint)hashCode << 16)) | (uint)input.Length;

        // Seqlock pattern: read sequence, speculatively read data, verify sequence unchanged.
        // This is lock-free for reads - no CAS required unless we need to write.
        ulong seq1 = Volatile.Read(ref e.Combined);

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
                    Thread.MemoryBarrier();

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
                    Thread.MemoryBarrier();

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
                    Thread.MemoryBarrier();

                if (seq1 == Volatile.Read(ref e.Combined) &&
                    MemoryMarshal.CreateReadOnlySpan(ref copy.Start, input.Length).SequenceEqual(input))
                {
                    keccak256 = cachedKeccak;
                    return;
                }
            }
        }

        keccak256 = ValueKeccak.Compute(input);

        ulong existing = Volatile.Read(ref e.Combined);

        // Skip write if hash/length match (regardless of version) - reduces cache line invalidation.
        if ((existing & HashLengthMask) == combined)
        {
            return;
        }

        // Increment 8-bit version counter (wraps after 256) to detect ABA in seqlock readers.
        // 256 writes needed to wrap = ~8-13μs, far exceeding ~50ns read window.
        ulong newVersion = ((existing & VersionMask) + VersionIncrement) & VersionMask;
        ulong toStore = combined | newVersion;

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

        private const int PayloadStart = sizeof(ulong);
        private const int ValueStart = Size - ValueHash256.MemorySize;
        public const int MaxPayloadLength = ValueStart - PayloadStart;

        /// <summary>
        /// Represents a combined value for: hash, length and a potential <see cref="KeccakCache.LockMarker"/>.
        /// </summary>
        [FieldOffset(0)] public ulong Combined;

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
