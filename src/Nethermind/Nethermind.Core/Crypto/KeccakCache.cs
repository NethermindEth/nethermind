// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto;

/// <summary>
/// This is a minimalistic one-way set associative cache for Keccak values.
///
/// It allocates only 8MB of memory to store 64k of entries.
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
    private const int BucketMask = 0x0000_FFFF;

    private const int InputLengthOfKeccak = ValueHash256.MemorySize;
    private const int InputLengthOfAddress = Address.Size;

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
        ComputeTo(input, out ValueHash256 keccak256);
        return keccak256;
    }

    [SkipLocalsInit]
    public static void ComputeTo(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
    {
        // Special cases jump forward as unpredicted
        if (input.Length == 0 || input.Length > Entry.MaxPayloadLength)
        {
            goto Uncommon;
        }

        int hashCode = input.FastHash();
        uint index = (uint)hashCode & BucketMask;

        Debug.Assert(index < Count);

        ref Entry e = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(Memory), index);

        // Read aligned, volatile, won't be torn, check with computed
        if (Volatile.Read(ref e.HashCode) == hashCode)
        {
            // There's a possibility of a hit, try lock.
            int lockAndLength = Volatile.Read(ref e.LockAndLength);
            // Lock by negating length if the length is the same size as input.
            if (lockAndLength == input.Length && Interlocked.CompareExchange(ref e.LockAndLength, -lockAndLength, lockAndLength) == lockAndLength)
            {
                if (e.HashCode != hashCode)
                {
                    // The value has been changed between reading and taking a lock.
                    // Release the lock and compute.
                    Volatile.Write(ref e.LockAndLength, lockAndLength);
                    goto Compute;
                }

                // Take local copy of the payload and hash, to release the lock as soon as possible and make a key comparison without holding it.
                // Local copy of 8+16+64 payload bytes.
                Payload copy = e.Value;
                // Copy Keccak256 directly to the local return variable, since we will overwrite if no match anyway.
                keccak256 = e.Keccak256;

                // Release the lock, by setting back to unnegated length.
                Volatile.Write(ref e.LockAndLength, lockAndLength);

                // Lengths are equal, the input length can be used without any additional operation.
                if (input.Length == InputLengthOfKeccak)
                {
                    // Hashing UInt256 or Hash256 which is Vector256
                    if (Unsafe.As<byte, Vector256<byte>>(ref copy.Start) ==
                        Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(input)))
                    {
                        // Current keccak256 is correct hash.
                        return;
                    }
                }
                else if (input.Length == InputLengthOfAddress)
                {
                    // Hashing Address
                    ref byte bytes0 = ref copy.Start;
                    ref byte bytes1 = ref MemoryMarshal.GetReference(input);
                    // 20 bytes which is Vector128+uint
                    if (Unsafe.As<byte, Vector128<byte>>(ref bytes0) == Unsafe.As<byte, Vector128<byte>>(ref bytes1) &&
                        Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes0, Vector128<byte>.Count)) ==
                            Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes1, Vector128<byte>.Count)))
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
        }

    Compute:
        keccak256 = ValueKeccak.Compute(input);

        // Try lock and memoize
        int length = Volatile.Read(ref e.LockAndLength);
        // Negative value means that the entry is locked, set to int.MinValue to avoid confusion empty entry,
        // since we are overwriting it anyway e.g. -0 would not be a reliable locked state.
        if (length >= 0 && Interlocked.CompareExchange(ref e.LockAndLength, int.MinValue, length) == length)
        {
            e.HashCode = hashCode;
            e.Keccak256 = keccak256;

            // Fast copy for 2 common sizes
            if (input.Length == InputLengthOfKeccak)
            {
                // UInt256 or Hash256 which is Vector256
                Unsafe.As<byte, Vector256<byte>>(ref e.Value.Start) =
                    Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(input));
            }
            else if (input.Length == InputLengthOfAddress)
            {
                // Address
                ref byte bytes0 = ref e.Value.Start;
                ref byte bytes1 = ref MemoryMarshal.GetReference(input);
                // 20 bytes which is Vector128+uint
                Unsafe.As<byte, Vector128<byte>>(ref bytes0) = Unsafe.As<byte, Vector128<byte>>(ref bytes1);
                Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes0, Vector128<byte>.Count)) =
                    Unsafe.As<byte, uint>(ref Unsafe.Add(ref bytes1, Vector128<byte>.Count));
            }
            else
            {
                // Non 32 byte or 20 byte input; call CopyTo
                input.CopyTo(MemoryMarshal.CreateSpan(ref e.Value.Start, input.Length));
            }

            // Release the lock, input.Length is always positive so setting it is enough.
            Volatile.Write(ref e.LockAndLength, input.Length);
        }
        return;

    Uncommon:
        if (input.Length == 0)
        {
            keccak256 = ValueKeccak.OfAnEmptyString;
        }
        else
        {
            keccak256 = ValueKeccak.Compute(input);
        }
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
        /// Should work for both ARM and x64 and be aligned.
        /// </summary>
        public const int Size = 128;

        private const int PayloadStart = 8;
        private const int ValueStart = Size - ValueHash256.MemorySize;
        public const int MaxPayloadLength = ValueStart - PayloadStart;

        /// <summary>
        /// Length is always positive so we can use a negative length to indicate that it is locked.
        /// </summary>
        [FieldOffset(0)]
        public int LockAndLength;

        /// <summary>
        /// The fast Crc32c of the Value
        /// </summary>
        [FieldOffset(4)]
        public int HashCode;

        /// <summary>
        /// The actual value
        /// </summary>
        /// Alignments 8+16+64
        [FieldOffset(PayloadStart)]
        public Payload Value;

        /// <summary>
        /// The Keccak of the Value
        /// </summary>
        [FieldOffset(ValueStart)]
        public ValueHash256 Keccak256;
    }

    [InlineArray(Entry.MaxPayloadLength)]
    private struct Payload
    {
        public byte Start;
    }
}
