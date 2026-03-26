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
    public const nuint Count = BucketMask + 1;

    private const int BucketMask = 0x0001_FFFF;
    private const uint HashMask = unchecked((uint)~BucketMask);
    private const uint LockMarker = 0x0000_8000;
    private const uint VersionMask = 0x0000_7F80;       // Bits 7-14: 8-bit version counter (0-255)
    private const uint VersionIncrement = 0x0000_0080;  // Bit 7: increment step for version
    private const uint HashLengthMask = HashMask | 0x7F;  // Hash (bits 17-31) + Length (bits 0-6)

    private const int InputLengthOfKeccak = ValueHash256.MemorySize;
    private const int InputLengthOfAddress = Address.Size;
    private const int CacheLineSizeBytes = 64;

#if ZK_EVM
    // zkEVM: avoid NativeMemory.AlignedAlloc (can fault in some environments). Use managed pinned storage instead.
    private static readonly byte[] ManagedBuffer;
    private static readonly GCHandle ManagedHandle;
#endif
    private static readonly Entry* Memory;

    static KeccakCache()
    {
        const nuint size = Count * Entry.Size;

#if ZK_EVM
        ManagedBuffer = GC.AllocateArray<byte>((int)size, pinned: true);
        ManagedHandle = GCHandle.Alloc(ManagedBuffer, GCHandleType.Pinned);
        Memory = (Entry*)ManagedHandle.AddrOfPinnedObject();
#else
        // Aligned, so that no torn reads if fields of Entry are properly aligned.
        Memory = (Entry*)NativeMemory.AlignedAlloc(size, BitOperations.RoundUpToPowerOf2(Entry.Size));
        NativeMemory.Clear(Memory, size);
        GC.AddMemoryPressure((long)size);
#endif
    }

    [SkipLocalsInit]
    public static ValueHash256 Compute(ReadOnlySpan<byte> input)
    {
        Span<byte> o = stackalloc byte[32];// key.BytesAsSpan;
        ZiskBindings.Crypto.keccak256_c(input, (nuint)input.Length, o);
        return new ValueHash256(o);
    }

    [SkipLocalsInit]
    public static void ComputeTo(ReadOnlySpan<byte> input, out ValueHash256 keccak256)
    {
        Span<byte> o = stackalloc byte[32];// key.BytesAsSpan;
        ZiskBindings.Crypto.keccak256_c(input, (nuint)input.Length, o);
        keccak256 = new ValueHash256(o);
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
