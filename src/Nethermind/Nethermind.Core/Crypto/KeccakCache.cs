// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto;

/// <summary>
/// This is a minimalistic one-way set associative cache for Keccak values.
///
/// It allocates only 8MB of memory to store 64k of entries.
/// No misaligned reads. Everything is aligned to both cache lines as well as to boundaries so no torn reads.
/// Requires a single CAS to lock and <see cref="Volatile.Write(ref int,int)"/> to unlock.
/// On lock failure, it just moves on with execution.
/// Uses copying on the stack to get the entry, have it copied and release the lock ASAP. This is 128 bytes to copy that quite likely will be the hit.
/// </summary>
public static unsafe class KeccakCache
{
    /// <summary>
    /// Count is defined as a +1 over bucket mask. In the future, just change the mask as the main parameter.
    /// </summary>
    public const int Count = BucketMask + 1;
    private const int BucketMask = 0x0000_FFFF;
    private const uint HashMask = unchecked((uint)~BucketMask);

    private static readonly Entry* Memory;

    static KeccakCache()
    {
        const UIntPtr size = Count * Entry.Size;

        // Aligned, so that no torn reads if fields of Entry are properly aligned.
        Memory = (Entry*)NativeMemory.AlignedAlloc(size, Entry.Size);
        NativeMemory.Clear(Memory, size);
    }

    [SkipLocalsInit]
    public static ValueHash256 Compute(ReadOnlySpan<byte> input)
    {
        Unsafe.SkipInit(out ValueHash256 hash);

        // Special cases first
        if (input.Length == 0)
        {
            hash = ValueKeccak.OfAnEmptyString;
            goto Return;
        }

        if (input.Length > Entry.MaxPayloadLength)
        {
            hash = ValueKeccak.Compute(input);
            goto Return;
        }

        uint fast = (uint)input.FastHash();
        uint index = fast & BucketMask;

        Debug.Assert(index < Count);

        uint hashAndLength = (fast & HashMask) | (ushort)input.Length;

        ref Entry e = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(Memory), index);

        // Read aligned, volatile, won't be torn, check with computed
        if (Volatile.Read(ref e.HashAndLength) == hashAndLength)
        {
            // There's a possibility of a hit, try lock.
            if (Interlocked.CompareExchange(ref e.Lock, Entry.Locked, Entry.Unlocked) == Entry.Unlocked)
            {
                if (e.HashAndLength != hashAndLength)
                {
                    // The value has been changed between reading and taking a lock.
                    // Release the lock and compute.
                    Volatile.Write(ref e.Lock, Entry.Unlocked);
                    goto Compute;
                }

                // Local copy of 128 bytes, to release the lock as soon as possible and make a key comparison without holding it.
                Entry copy = e;

                // Release the lock
                Volatile.Write(ref e.Lock, Entry.Unlocked);

                // Lengths are equal, the input length can be used without any additional operation.
                if (MemoryMarshal.CreateReadOnlySpan(ref copy.Payload, input.Length).SequenceEqual(input))
                {
                    hash = copy.Value;
                    goto Return;
                }
            }
        }

    Compute:
        hash = ValueKeccak.Compute(input);

        // Try lock and memoize
        if (Interlocked.CompareExchange(ref e.Lock, Entry.Locked, Entry.Unlocked) == Entry.Unlocked)
        {
            e.HashAndLength = hashAndLength;
            e.Value = hash;

            input.CopyTo(MemoryMarshal.CreateSpan(ref e.Payload, input.Length));

            // Release the lock
            Volatile.Write(ref e.Lock, Entry.Unlocked);
        }

    Return:
        return hash;
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
        public const int Unlocked = 0;
        public const int Locked = 1;

        /// <summary>
        /// Should work for both ARM and x64 and be aligned.
        /// </summary>
        public const int Size = 128;

        private const int PayloadStart = 8;
        private const int ValueStart = Size - ValueHash256.MemorySize;
        public const int MaxPayloadLength = ValueStart - PayloadStart;

        [FieldOffset(0)]
        public int Lock;

        /// <summary>
        /// The mix of hash and length allows for a fast comparison and a single volatile read.
        /// The length is encoded as the low part, while the hash as the high part of uint.
        /// </summary>
        [FieldOffset(4)]
        public uint HashAndLength;

        [FieldOffset(PayloadStart)]
        public byte Payload;

        /// <summary>
        /// The actual value
        /// </summary>
        [FieldOffset(ValueStart)]
        public ValueHash256 Value;
    }
}
