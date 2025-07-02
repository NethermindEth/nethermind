// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto;

/// <summary>
/// This is a minimalistic one-way set associative cache for Keccak values.
///
/// It allocates only 12MB of memory to store 128k of entries.
/// No misaligned reads. Everything is aligned to both cache lines as well as to boundaries so no torn reads.
/// Requires a single CAS to lock and <see cref="Volatile.Write(ref int,int)"/> to unlock.
/// On a lock failure, it just moves on with execution.
/// When a potential hit happens, the value of the result and the value of the key are copied on the stack to release the lock ASAP.
/// </summary>
public static unsafe class KeccakCache
{
    /// <summary>
    /// Count is defined as a +1 over bucket mask. In the future, just change the mask as the main parameter.
    /// </summary>
    public const int Count = BucketMask + 1;

    private const int BucketMask = 0x0001_FFFF;
    private const uint HashMask = unchecked((uint)~BucketMask);
    private const uint LockMarker = 0x0000_8000;

    private const int InputLengthOfKeccak = ValueHash256.MemorySize;
    private const int InputLengthOfAddress = Address.Size;

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

        // Half the hash his encoded in the bucket so we only need half of it and can use other half for length.
        // This allows to create a combined value that represents a part of the hash, the input's length and the lock marker.
        uint combined = (HashMask & (uint)hashCode) | (uint)input.Length;

        // Compare with volatile read and then try to lock with CAS
        if (Volatile.Read(ref e.Combined) == combined &&
            Interlocked.CompareExchange(ref e.Combined, combined | LockMarker, combined) == combined)
        {
            // Combined is equal to existing, meaning that it was not locked, and both the length and the hash were equal.

            // Take local copy of the payload and hash, to release the lock as soon as possible and make a key comparison without holding it.
            // Local copy of 8+16+64 payload bytes.
            Payload copy = e.Value;
            // Copy Keccak256 directly to the local return variable, since we will overwrite if no match anyway.
            keccak256 = e.Keccak256;

            // Release the lock, by setting back to the combined value.
            Volatile.Write(ref e.Combined, combined);

            // Lengths are equal, the input length can be used without any additional operation.
            if (input.Length == InputLengthOfKeccak)
            {
                // Hashing UInt256 or Hash256 which is Vector256
                if (Unsafe.As<byte, Vector256<byte>>(ref copy.Aligned32) ==
                    Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(input)))
                {
                    // Current keccak256 is correct hash.
                    return;
                }
            }
            else if (input.Length == InputLengthOfAddress)
            {
                // Hashing Address
                ref byte bytes1 = ref MemoryMarshal.GetReference(input);
                // 20 bytes which is uint+Vector128
                if (Unsafe.As<byte, uint>(ref copy.Start) == Unsafe.As<byte, uint>(ref bytes1) &&
                    Unsafe.As<byte, Vector128<byte>>(ref copy.Aligned32) ==
                        Unsafe.As<byte, Vector128<byte>>(ref Unsafe.Add(ref bytes1, sizeof(uint))))
                {
                    // Current keccak256 is correct hash.
                    return;
                }
            }
            else if (MemoryMarshal.CreateReadOnlySpan(ref copy.Start, input.Length).SequenceEqual(input))
            {
                // Non 32 byte or 20 byte input; call SequenceEqual.
                // Current keccak256 is correct hash.
                return;
            }
        }

        keccak256 = ValueKeccak.Compute(input);

        uint existing = Volatile.Read(ref e.Combined);

        // Try to set to the combined locked state, if not already locked.
        if ((existing & LockMarker) == 0 && Interlocked.CompareExchange(ref e.Combined, combined | LockMarker, existing) == existing)
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

            // Release the lock, by setting back to the combined value.
            Volatile.Write(ref e.Combined, combined);
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
        /// The size will make it 1.5 CPU cache entry or 0.75 which may result in some collisions.
        /// Still, it's better to save these 32 bytes per entry and have a bigger cache.
        /// </summary>
        public const int Size = 96;

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
