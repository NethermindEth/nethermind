// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

public struct EvmPooledMemory
{
    public const int WordSize = 32;
    internal const ulong MaxMemorySize = int.MaxValue - WordSize + 1;
    internal const long MaxMemoryWords = (int.MaxValue - WordSize + 1L) / WordSize;

    // Pooled buffers are returned dirty; a fresh frame owns a stale buffer and only lazily zeroes the
    // parts that are exposed-but-not-written. _validUpTo is the prefix [0, _validUpTo) that already reads
    // as zero where this frame did not write it. Reads validate up to their end; writes clear only the
    // head gap below the write. Contiguous, word-aligned writes (the common case) clear nothing.
    private ulong _validUpTo;

    private byte[]? _memory;
    public ulong Size { get; private set; }

    public bool TrySaveWord(in UInt256 location, Span<byte> word)
    {
        if (word.Length != WordSize) ThrowArgumentOutOfRangeException();

        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength, out bool outOfGas);
        if (outOfGas) return false;

        int offset = TruncateToInt32(location.u0);
        EvmWord word1 = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        UpdateSize(newLength);
        PrepareWrite(offset, WordSize);
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, offset), word1);
        return true;
    }

    public bool TrySaveByte(in UInt256 location, byte value)
    {
        CheckMemoryAccessViolation(in location, 1, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        int offset = TruncateToInt32(location.u0);
        UpdateSize(newLength);
        PrepareWrite(offset, 1);
        _memory![offset] = value;
        return true;
    }

    public bool TrySave(in UInt256 location, Span<byte> value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        CheckMemoryAccessViolation(in location, (ulong)value.Length, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        int offset = TruncateToInt32(location.u0);
        UpdateSize(newLength);
        PrepareWrite(offset, value.Length);
        value.CopyTo(_memory.AsSpan(offset, value.Length));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckMemoryAccessViolation(in UInt256 location, in UInt256 length, out ulong newLength, out bool isViolation)
    {
        if (!length.IsUint64)
        {
            isViolation = true;
            newLength = 0;
            return;
        }

        CheckMemoryAccessViolation(in location, length.u0, out newLength, out isViolation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckMemoryAccessViolation(in UInt256 location, ulong length, out ulong newLength, out bool isViolation)
    {
        // First pass: bail if length exceeds the aligned-memory cap or the location isn't a u64.
        // Checking length first lets the compiler drop this branch entirely at call sites with a
        // constant length (MSTORE/MSTORE8/MLOAD pass 32 or 1).
        if (length > MaxMemorySize || !location.IsUint64)
        {
            isViolation = true;
            newLength = 0;
            return;
        }

        // length <= MaxMemorySize, so (MaxMemorySize - length) does not underflow. This single
        // comparison subsumes both the unsigned-overflow check and the final bounds check that the
        // original code wrote as two separate branches.
        ulong offset = location.u0;
        if (offset > MaxMemorySize - length)
        {
            isViolation = true;
            newLength = 0;
            return;
        }

        // locU0 + length <= MaxMemorySize < 2^31, no overflow possible.
        isViolation = false;
        newLength = offset + length;
    }

    public bool TrySave(in UInt256 location, byte[] value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        ulong length = (ulong)value.Length;
        CheckMemoryAccessViolation(in location, length, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        int offset = TruncateToInt32(location.u0);
        UpdateSize(newLength);
        PrepareWrite(offset, value.Length);
        Array.Copy(value, 0, _memory!, offset, value.Length);
        return true;
    }

    public bool TrySave(in UInt256 location, in ZeroPaddedSpan value)
    {
        if (value.Length == 0)
        {
            // Nothing to do
            return true;
        }

        ulong length = (ulong)value.Length;
        CheckMemoryAccessViolation(in location, length, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        int intLocation = TruncateToInt32(location.u0);
        UpdateSize(newLength);
        // The span plus its zero padding fully cover [intLocation, intLocation + value.Length).
        PrepareWrite(intLocation, value.Length);
        value.Span.CopyTo(_memory.AsSpan(intLocation, value.Span.Length));
        if (value.PaddingLength > 0)
        {
            _memory.AsSpan(intLocation + value.Span.Length, value.PaddingLength).Clear();
        }

        return true;
    }

    /// <summary>
    /// Variant of <see cref="TrySave"/> requiring the caller to have already invoked
    /// <see cref="IGasPolicy{TSelf}.UpdateMemoryCost"/> for (<paramref name="location"/>,
    /// <paramref name="value"/>.Length) — which both bounds-checks and grows the memory —
    /// so this skips re-validation. Mirrors <see cref="CopyAfterGas"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SaveAfterGas(in UInt256 location, in ZeroPaddedSpan value)
    {
        if (value.Length == 0)
        {
            return;
        }

        Debug.Assert(location.IsUint64);
        Debug.Assert(location.u0 + (ulong)value.Length <= Size);

        int intLocation = TruncateToInt32(location.u0);
        // The span plus its zero padding fully cover [intLocation, intLocation + value.Length).
        PrepareWrite(intLocation, value.Length);
        int spanLength = value.Span.Length;
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);

        if (spanLength > 0)
        {
            value.Span.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref memory, intLocation), spanLength));
        }
        if (value.PaddingLength > 0)
        {
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref memory, intLocation + spanLength), value.PaddingLength).Clear();
        }
    }

    public bool TryLoadSpan(scoped in UInt256 location, out Span<byte> data)
    {
        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength, out bool isViolation);
        if (isViolation)
        {
            data = default;
            return false;
        }

        data = LoadSpan(newLength, TruncateToInt32(location.u0), WordSize);
        return true;
    }

    public bool TryLoadSpan(scoped in UInt256 location, scoped in UInt256 length, out Span<byte> data)
    {
        if (length.IsZero)
        {
            data = [];
            return true;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newLength, out bool isViolation);
        if (isViolation)
        {
            data = default;
            return false;
        }

        data = LoadSpan(newLength, TruncateToInt32(location.u0), TruncateToInt32(length.u0));
        return true;
    }

    public bool TryLoad(in UInt256 location, in UInt256 length, out ReadOnlyMemory<byte> data)
    {
        if (length.IsZero)
        {
            data = default;
            return true;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newLength, out bool isViolation);
        if (isViolation)
        {
            data = default;
            return false;
        }

        UpdateSize(newLength);
        PrepareRead(TruncateToInt32(newLength));

        data = _memory.AsMemory(TruncateToInt32(location.u0), TruncateToInt32(length.u0));
        return true;
    }

    public ReadOnlyMemory<byte> Inspect(in UInt256 location, in UInt256 length)
    {
        if (length.IsZero)
        {
            return default;
        }

        if (location > int.MaxValue)
        {
            return new byte[(long)length];
        }

        if (_memory is null)
        {
            return default;
        }

        if (location >= Size)
        {
            return default;
        }
        UInt256 largeSize = location + length;
        if (largeSize > _memory.Length)
        {
            return default;
        }

        ValidateForTracing((ulong)largeSize);
        return _memory.AsMemory((int)location, (int)length);
    }

    // Tracing reads [0, upTo) without growing Size; validate any exposed-but-unwritten bytes it would show.
    private void ValidateForTracing(ulong upTo)
    {
        if (_memory is null) return;
        int end = (int)Math.Min(upTo, (ulong)_memory.Length);
        int valid = (int)_validUpTo;
        if (end > valid)
        {
            ClearRange(valid, end - valid);
            _validUpTo = (ulong)end;
        }
    }

    public ulong CalculateMemoryCost(in UInt256 location, ulong length, out bool outOfGas)
    {
        if (length == 0)
        {
            outOfGas = false;
            return 0;
        }

        CheckMemoryAccessViolation(in location, length, out ulong newSize, out outOfGas);
        if (outOfGas) return 0;

        return newSize > Size ? ComputeMemoryExpansionCost(newSize) : 0;
    }

    public ulong CalculateMemoryCost(in UInt256 location, in UInt256 length, out bool outOfGas)
    {
        if (length.IsZero)
        {
            outOfGas = false;
            return 0;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newSize, out outOfGas);
        if (outOfGas) return 0;

        return newSize > Size ? ComputeMemoryExpansionCost(newSize) : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreWordAfterGas(in UInt256 location, ReadOnlySpan<byte> word)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        EvmWord value = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        PrepareWrite(offset, WordSize);
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, offset), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreByteAfterGas(in UInt256 location, byte value)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        PrepareWrite(offset, 1);
        _memory![offset] = value;
    }

    /// <summary>
    /// Returns a reference to the first of 32 contiguous bytes at <paramref name="location"/> in memory,
    /// expanding the buffer (and charging gas) as needed.
    /// </summary>
    /// <remarks>
    /// The returned ref aliases the internal memory buffer. The caller MUST consume the ref before
    /// performing any other operation that may re-rent or grow the underlying storage, otherwise the
    /// ref becomes dangling.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref byte Load32BytesAfterGas(in UInt256 location)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        PrepareRead(offset + WordSize);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory!), offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> LoadSpanAfterGas(in UInt256 location, ulong length)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        int intLength = TruncateToInt32(length);
        PrepareRead(offset + intLength);
        return _memory!.AsSpan(offset, intLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopyAfterGas(in UInt256 destination, in UInt256 source, ulong length)
    {
        if (length == 0)
        {
            return;
        }

        int destinationOffset = TruncateToInt32(destination.u0);
        int sourceOffset = TruncateToInt32(source.u0);
        int intLength = TruncateToInt32(length);

        // Source is read and destination is written; validate the whole span, then copy over it.
        PrepareRead(Math.Max(destinationOffset, sourceOffset) + intLength);
        _memory!.AsSpan(sourceOffset, intLength).CopyTo(_memory.AsSpan(destinationOffset, intLength));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong ComputeMemoryExpansionCost(ulong newSize)
    {
        // CheckMemoryAccessViolation has already capped newSize at MaxMemorySize (< 2^31), so the
        // ceiling division cannot overflow uint and the squared terms stay below 2^52. Size is
        // maintained as a word-aligned invariant by UpdateSize, so no ceiling is required there.
        Debug.Assert(newSize <= MaxMemorySize);
        Debug.Assert(Size % WordSize == 0);

        ulong newActiveWords = (newSize + (WordSize - 1UL)) >> 5;
        ulong activeWords = Size >> 5;

        // Full Yellow Paper memory cost is bounded above by ~8.8e12 gas, which fits comfortably
        // in ulong -- so the outOfGas propagation that older revisions carried is unreachable.
        // newActiveWords >= activeWords by the gating condition in UpdateSize, so the subtractions are safe.
        ulong cost = (newActiveWords - activeWords) * GasCostOf.Memory +
            ((newActiveWords * newActiveWords) >> 9) -
            ((activeWords * activeWords) >> 9);

        UpdateSize(newSize);

        return cost;
    }

    public TraceMemory GetTrace()
    {
        ulong size = Size;
        ValidateForTracing(size);
        return new(size, _memory);
    }

    public void Dispose()
    {
        byte[]? memory = _memory;

        if (memory is not null)
        {
            _memory = null;
            // Reset so a reused struct starts with an empty valid prefix even if the holder does not
            // reset it; the buffer is returned dirty and the next renter zeroes the gaps it exposes.
            _validUpTo = 0;
            Size = 0;
            ReturnDirty(memory);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateSize(ulong length)
    {
        // CheckMemoryAccessViolation has already proven length <= MaxMemorySize, so
        // (length + 31) cannot overflow. Branchless align-up replaces the original
        // "modulo + conditional" pair: one AND and one ADD, no jumps.
        if (length > Size)
        {
            Size = (length + (WordSize - 1UL)) & ~(WordSize - 1UL);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> LoadSpan(ulong newLength, int offset, int length)
    {
        UpdateSize(newLength);
        PrepareRead(offset + length);
        return _memory!.AsSpan(offset, length);
    }

    // Zero the exposed region below a write; a contiguous write (offset == valid extent) clears nothing.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareWrite(int offset, int length)
    {
        EnsureCapacity();
        int valid = (int)_validUpTo;
        if (offset > valid)
        {
            ClearRange(valid, offset - valid);
        }
        int end = offset + length;
        if (end > valid)
        {
            _validUpTo = (ulong)end;
        }
    }

    // A read fills nothing, so zero the whole newly-exposed region.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareRead(int end)
    {
        EnsureCapacity();
        int valid = (int)_validUpTo;
        if (end > valid)
        {
            ClearRange(valid, end - valid);
            _validUpTo = (ulong)end;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity()
    {
        byte[]? memory = _memory;
        if (memory is null || Size > (ulong)memory.Length)
        {
            RentSlow();
        }
    }

    // Small gaps (word padding, head gaps below jump/sparse writes) beyond which Span.Clear's memset
    // (rep-stosb / non-temporal, cache-friendly at size) wins over inline vector stores.
    private const int InlineClearThreshold = 512;

    // Zeroes [start, start + length). Clears up to InlineClearThreshold with inline vector stores to dodge
    // Span.Clear's call/dispatch overhead on the many small gaps; larger clears defer to Span.Clear.
    // Vector<byte> lowers to the widest SIMD available (128/256/512-bit), so no per-width paths are needed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void ClearRange(int start, int length)
    {
        int width = Vector<byte>.Count;
        // Branchless range check: width <= length <= InlineClearThreshold.
        if (Vector.IsHardwareAccelerated && (uint)(length - width) <= (uint)(InlineClearThreshold - width))
        {
            ref byte d = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory!), (nint)(uint)start);
            Vector<byte> zero = default;
            int last = length - width;
            for (int i = 0; i < last; i += width)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nint)i), zero);
            }
            // Final (possibly overlapping) store covers the tail shorter than one vector.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nint)last), zero);
        }
        else
        {
            _memory.AsSpan(start, length).Clear();
        }
    }

    private const int MinRentSize = 1_024;
    private const int MaxCachedArrayLength = 1 << 16;
    private const int CacheSlots = 16;

    [ThreadStatic] private static byte[]?[]? _pooledArrays;
    [ThreadStatic] private static int _pooledArrayCount;

    // Rent a (possibly dirty) buffer of at least minLength bytes. Callers zero the gaps they expose.
    private static byte[] RentDirty(int minLength)
    {
        byte[]?[]? cache = _pooledArrays;
        int count = _pooledArrayCount - 1;
        for (int i = count; i >= 0; i--)
        {
            byte[] candidate = cache![i]!;
            if (candidate.Length >= minLength)
            {
                _pooledArrayCount = count;
                cache[i] = cache[count];
                cache[count] = null;
                return candidate;
            }
        }

        if (minLength > MaxCachedArrayLength)
        {
            return RentLarge(minLength);
        }

        return new byte[BitOperations.RoundUpToPowerOf2((uint)minLength)];
    }

    private static void ReturnDirty(byte[] array)
    {
        if (array.Length > MaxCachedArrayLength)
        {
            ReturnLarge(array);
            return;
        }

        byte[]?[] cache = _pooledArrays ??= new byte[CacheSlots][];
        if (_pooledArrayCount < CacheSlots)
        {
            cache[_pooledArrayCount++] = array;
        }
    }

#if ZK_EVM
    private static byte[] RentLarge(int minLength) => SafeArrayPool<byte>.Shared.Rent(minLength);

    private static void ReturnLarge(byte[] array) => SafeArrayPool<byte>.Shared.Return(array);
#else
    private const int MaxSharedArrayLength = 1 << 20;
    // Above this, buffers fall back to plain allocation (not pooled), as before this change.
    private const int MaxLargePooledArrayLength = 1 << 22;
    private static readonly System.Buffers.ArrayPool<byte> _largeArrayPool =
        System.Buffers.ArrayPool<byte>.Create(maxArrayLength: MaxLargePooledArrayLength, maxArraysPerBucket: 16);

    private static byte[] RentLarge(int minLength)
        => minLength > MaxSharedArrayLength
            ? _largeArrayPool.Rent(minLength)
            : SafeArrayPool<byte>.Shared.Rent(minLength);

    private static void ReturnLarge(byte[] array)
    {
        if (array.Length > MaxSharedArrayLength)
            _largeArrayPool.Return(array);
        else
            SafeArrayPool<byte>.Shared.Return(array);
    }
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow()
    {
        if (_memory is null)
        {
            _memory = RentDirty((int)Math.Max((uint)Size, MinRentSize));
        }
        else if (Size > (ulong)_memory.LongLength)
        {
            byte[] beforeResize = _memory;
            _memory = RentDirty(TruncateToInt32(Size));
            // Preserve only the valid prefix; the rest is re-zeroed lazily as it is exposed.
            Array.Copy(beforeResize, 0, _memory, 0, (int)_validUpTo);
            ReturnDirty(beforeResize);
        }
    }

    // (int)(uint)value rather than (int)value: RyuJIT emits noticeably worse codegen for a
    // direct ulong->int narrowing (treats it as a signed truncation and keeps the operation
    // on 64-bit registers, sometimes with extra moves or a movsxd). Routing through (uint)
    // lowers to a plain 32-bit register write, which on x64 implicitly zeros the upper 32
    // bits - the JIT collapses it to a single mov. The subsequent (int) reinterpret is free
    // (same bit pattern). CheckMemoryAccessViolation caps addressable memory at MaxMemorySize
    // (< 2^31), so reinterpreting the low word as signed is always safe here.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TruncateToInt32(ulong value) => (int)(uint)value;

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowArgumentOutOfRangeException()
    {
        Metrics.EvmExceptions++;
        throw new ArgumentOutOfRangeException("EvmWord size must be 32 bytes");
    }
}

public static class UInt256Extensions
{
    extension(in UInt256 value)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ToLong() => !value.IsUint64 || value.u0 > long.MaxValue ? long.MaxValue : (long)value.u0;
    }
}
