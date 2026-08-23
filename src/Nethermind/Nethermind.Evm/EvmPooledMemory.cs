// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    // Matches the minimum rental tier, avoiding an earlier spill boundary for small frames.
    internal const int InlineCapacity = 1024;
    internal const ulong MaxMemorySize = int.MaxValue - WordSize + 1;
    internal const long MaxMemoryWords = (int.MaxValue - WordSize + 1L) / WordSize;

    // Bytes below this prefix are valid; Size may exceed both it and the backing array until a read
    // materializes the logical zero tail.
    private ulong _initializedSize;

    private MemoryManager<byte>? _inlineMemoryManager;
    private byte[]? _memory;
    public ulong Size { get; private set; }

    internal EvmPooledMemory(MemoryManager<byte> inlineMemoryManager, bool isFresh = false)
    {
        _initializedSize = isFresh ? (ulong)InlineCapacity : 0;
        _inlineMemoryManager = inlineMemoryManager;
        _memory = null;
        Size = 0;
    }

    public bool TrySaveWord(in UInt256 location, Span<byte> word)
    {
        if (word.Length != WordSize) ThrowArgumentOutOfRangeException();

        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength, out bool outOfGas);
        if (outOfGas) return false;

        int offset = TruncateToInt32(location.u0);
        EvmWord word1 = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        UpdateSize(newLength);
        ref byte memory = ref GetBackingReference();
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, offset), word1);
        return true;
    }

    public bool TrySaveByte(in UInt256 location, byte value)
    {
        CheckMemoryAccessViolation(in location, 1, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        int offset = TruncateToInt32(location.u0);
        UpdateSize(newLength);
        Unsafe.Add(ref GetBackingReference(), offset) = value;
        return true;
    }

    public bool TrySave(in UInt256 location, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        CheckMemoryAccessViolation(in location, (ulong)value.Length, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        UpdateSize(newLength, rentIfNeeded: false);
        SaveAfterGas(in location, value);
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
        => TrySave(in location, value.AsSpan());

    /// <summary>
    /// Variant of <see cref="TrySave"/> requiring the caller to have already invoked
    /// <see cref="IGasPolicy{TSelf}.UpdateMemoryCost"/> for (<paramref name="location"/>,
    /// <paramref name="value"/>.Length), which bounds-checks and updates the logical memory size,
    /// so this skips re-validation. Mirrors <see cref="CopyAfterGas"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SaveAfterGas(in UInt256 location, ReadOnlySpan<byte> value)
    {
        int length = value.Length;
        if (length == 0)
        {
            return;
        }

        Debug.Assert(location.IsUint64);
        Debug.Assert(location.u0 + (ulong)length <= Size);
        int intLocation = TruncateToInt32(location.u0);
        ulong preparedInitializedSize = PrepareOverwriteAfterGas(location.u0, (ulong)length);
        value.CopyTo(GetBackingSpan(intLocation, length));
        CommitOverwrite(preparedInitializedSize);
    }

    /// <summary>
    /// Copies a source range to memory and fills any bytes beyond the source with zeroes.
    /// The caller must have already charged for and expanded the destination range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopyFromZeroExtendedAfterGas(
        in UInt256 destination,
        ReadOnlySpan<byte> source,
        in UInt256 sourceOffset,
        int length)
    {
        if (length == 0)
        {
            return;
        }

        Debug.Assert(destination.IsUint64);
        Debug.Assert(destination.u0 + (ulong)length <= Size);
        ulong preparedInitializedSize = PrepareOverwriteAfterGas(destination.u0, (ulong)length);
        Span<byte> target = GetBackingSpan(TruncateToInt32(destination.u0), length);
        int copiedLength = 0;
        if (sourceOffset < source.Length)
        {
            int intSourceOffset = TruncateToInt32(sourceOffset.u0);
            copiedLength = Math.Min(source.Length - intSourceOffset, length);
            source.Slice(intSourceOffset, copiedLength).CopyTo(target);
        }

        if (copiedLength != length)
        {
            target[copiedLength..].Clear();
        }

        CommitOverwrite(preparedInitializedSize);
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

        data = GetBackingMemory(TruncateToInt32(location.u0), TruncateToInt32(length.u0));
        return true;
    }

    /// <summary>Loads a range that remains valid after this memory owner is reused.</summary>
    internal bool TryLoadOwned(in UInt256 location, in UInt256 length, out ReadOnlyMemory<byte> data)
    {
        if (!TryLoad(in location, in length, out data))
        {
            return false;
        }

        if (_memory is null && !data.IsEmpty)
        {
            data = data.ToArray();
        }

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

        if (GetBackingCapacity() == 0)
        {
            return default;
        }

        if (location >= Size)
        {
            return default;
        }
        UInt256 largeSize = location + length;
        if (largeSize > GetBackingCapacity())
        {
            return default;
        }

        ClearForTracing((ulong)largeSize);
        return GetBackingMemory((int)location, (int)length);
    }

    private void ClearForTracing(ulong size)
    {
        ulong capacity = GetBackingCapacity();
        if (capacity != 0 && size > _initializedSize)
        {
            int lengthToClear = (int)(Math.Min(size, capacity) - _initializedSize);
            GetBackingSpan((int)_initializedSize, lengthToClear).Clear();
            _initializedSize += (uint)lengthToClear;
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

    /// <summary>Stores a 32-byte word after memory expansion gas has been charged.</summary>
    /// <remarks>
    /// <paramref name="word"/> must not alias this memory instance because preparing the destination
    /// can replace and return the underlying buffer before the source bytes are read.
    /// </remarks>
    /// <param name="location">The start of the destination word.</param>
    /// <param name="word">The 32 source bytes to store.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreWordAfterGas(in UInt256 location, ReadOnlySpan<byte> word)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        ulong overwriteEnd = location.u0 + WordSize;
        byte[]? memory = _memory;
        if (memory is not null && Size <= (ulong)memory.Length && Size <= _initializedSize)
        {
            EvmWord value = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
            ref byte memoryData = ref MemoryMarshal.GetArrayDataReference(memory);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref memoryData, offset), value);
            return;
        }

        if (memory is null && _inlineMemoryManager is not null && overwriteEnd <= InlineCapacity)
        {
            ulong initializedSize = _initializedSize;
            if (location.u0 > initializedSize)
            {
                GetInlineSpan().Slice((int)initializedSize, offset - (int)initializedSize).Clear();
            }

            EvmWord inlineValue = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
            Unsafe.WriteUnaligned(ref GetInlineSpan()[offset], inlineValue);
            _initializedSize = Math.Max(initializedSize, overwriteEnd);
            return;
        }

        StoreWordSlow(overwriteEnd, offset, word);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StoreWordSlow(ulong overwriteEnd, int offset, ReadOnlySpan<byte> word)
    {
        PrepareAccessAfterGas(overwriteEnd);
        EvmWord value = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, offset), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreByteAfterGas(in UInt256 location, byte value)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        ulong overwriteEnd = location.u0 + 1;
        byte[]? memory = _memory;
        if (memory is not null && Size <= (ulong)memory.Length && Size <= _initializedSize)
        {
            ref byte memoryData = ref MemoryMarshal.GetArrayDataReference(memory);
            Unsafe.Add(ref memoryData, offset) = value;
            return;
        }

        if (memory is null && _inlineMemoryManager is not null && overwriteEnd <= InlineCapacity)
        {
            ulong initializedSize = _initializedSize;
            if (location.u0 > initializedSize)
            {
                GetInlineSpan().Slice((int)initializedSize, offset - (int)initializedSize).Clear();
            }

            GetInlineSpan()[offset] = value;
            _initializedSize = Math.Max(initializedSize, overwriteEnd);
            return;
        }

        StoreByteSlow(overwriteEnd, offset, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StoreByteSlow(ulong overwriteEnd, int offset, byte value)
    {
        PrepareAccessAfterGas(overwriteEnd);
        // The after-gas contract proves offset < Size; preparation guarantees overwriteEnd <= _memory.Length.
        ref byte memory = ref MemoryMarshal.GetArrayDataReference(_memory!);
        Unsafe.Add(ref memory, offset) = value;
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
        PrepareAccessAfterGas(location.u0 + WordSize);
        return ref Unsafe.Add(ref GetBackingReference(), offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> LoadSpanAfterGas(in UInt256 location, ulong length)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        int intLength = TruncateToInt32(length);
        PrepareAccessAfterGas(location.u0 + length);
        return GetBackingSpan(offset, intLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopyAfterGas(in UInt256 destination, in UInt256 source, ulong length)
    {
        if (length == 0)
        {
            return;
        }

        Debug.Assert(destination.IsUint64);
        Debug.Assert(source.IsUint64);
        Debug.Assert(destination.u0 + length <= Size);
        Debug.Assert(source.u0 + length <= Size);
        ulong sourceEnd = source.u0 + length;
        ulong destinationEnd = destination.u0 + length;
        ulong initializedSize = _initializedSize;
        byte[]? memory = _memory;
        if (memory is not null
            && sourceEnd <= initializedSize
            && destinationEnd <= (ulong)memory.Length
            && destination.u0 <= initializedSize)
        {
            int intLength = TruncateToInt32(length);
            memory.AsSpan(TruncateToInt32(source.u0), intLength)
                .CopyTo(memory.AsSpan(TruncateToInt32(destination.u0), intLength));
            if (destinationEnd > initializedSize)
            {
                _initializedSize = destinationEnd;
            }

            return;
        }

        CopyAfterGasSlow(in destination, in source, length, sourceEnd);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CopyAfterGasSlow(in UInt256 destination, in UInt256 source, ulong length, ulong sourceEnd)
    {
        if (sourceEnd > _initializedSize)
        {
            if (length == WordSize)
            {
                CopyPartiallyInitializedWordAfterGas(in destination, in source);
            }
            else
            {
                CopyPartiallyInitializedAfterGas(in destination, in source, length);
            }

            return;
        }

        int intLength = TruncateToInt32(length);
        ulong preparedInitializedSize = PrepareOverwriteAfterGas(
            destination.u0,
            length,
            sourceEnd);
        Span<byte> target = GetBackingSpan(TruncateToInt32(destination.u0), intLength);
        GetBackingSpan(TruncateToInt32(source.u0), intLength).CopyTo(target);
        CommitOverwrite(preparedInitializedSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyPartiallyInitializedWordAfterGas(in UInt256 destination, in UInt256 source)
    {
        ulong sourceAvailable = source.u0 < _initializedSize ? _initializedSize - source.u0 : 0;
        ulong preparedInitializedSize = PrepareOverwriteAfterGas(
            destination.u0,
            WordSize,
            sourceAvailable == 0 ? 0 : _initializedSize);
        Span<byte> target = GetBackingSpan(TruncateToInt32(destination.u0), WordSize);

        if (sourceAvailable != 0)
        {
            GetBackingSpan(TruncateToInt32(source.u0), TruncateToInt32(sourceAvailable)).CopyTo(target);
        }

        target[TruncateToInt32(sourceAvailable)..].Clear();
        CommitOverwrite(preparedInitializedSize);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CopyPartiallyInitializedAfterGas(in UInt256 destination, in UInt256 source, ulong length)
    {
        int intLength = TruncateToInt32(length);
        ulong sourceAvailable = source.u0 < _initializedSize
            ? Math.Min(length, _initializedSize - source.u0)
            : 0;
        ulong preparedInitializedSize = PrepareOverwriteAfterGas(
            destination.u0,
            length,
            sourceAvailable == 0 ? 0 : _initializedSize);
        Span<byte> target = GetBackingSpan(TruncateToInt32(destination.u0), intLength);

        if (sourceAvailable != 0)
        {
            GetBackingSpan(TruncateToInt32(source.u0), TruncateToInt32(sourceAvailable)).CopyTo(target);
        }

        if (sourceAvailable != length)
        {
            target[TruncateToInt32(sourceAvailable)..].Clear();
        }

        CommitOverwrite(preparedInitializedSize);
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
        Size = newActiveWords << 5;

        // Full Yellow Paper memory cost is bounded above by ~8.8e12 gas, which fits comfortably
        // in ulong -- so the outOfGas propagation that older revisions carried is unreachable.
        // newActiveWords >= activeWords by the caller's gating condition, so the subtractions are safe.
        ulong cost = (newActiveWords - activeWords) * GasCostOf.Memory +
            ((newActiveWords * newActiveWords) >> 9) -
            ((activeWords * activeWords) >> 9);

        return cost;
    }

    private static readonly TraceMemory EmptyTraceMemory = new(0, default);

    public TraceMemory GetTrace()
    {
        ulong size = Size;
        if (size == 0)
            return EmptyTraceMemory;

        ClearForTracing(size);
        // Clamp to Size so TraceMemory.Slice past the EVM high-water cannot see dirty tail bytes.
        ulong capacity = GetBackingCapacity();
        if (capacity == 0)
            return new(size, default);

        int visible = (int)Math.Min(size, capacity);
        return new(size, GetBackingMemory(0, visible));
    }

    public void Dispose()
    {
        byte[]? memory = _memory;

        if (memory is not null)
        {
            _memory = null;
            Return(memory);
        }

        _initializedSize = 0;
        Size = 0;
    }

    private void UpdateSize(ulong length, bool rentIfNeeded = true)
    {
        // CheckMemoryAccessViolation has already proven length <= MaxMemorySize, so
        // (length + 31) cannot overflow. Branchless align-up replaces the original
        // "modulo + conditional" pair: one AND and one ADD, no jumps.
        if (length > Size)
        {
            Size = (length + (WordSize - 1UL)) & ~(WordSize - 1UL);
        }

        if (rentIfNeeded)
        {
            EnsureRented(length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> LoadSpan(ulong newLength, int offset, int length)
    {
        UpdateSize(newLength);
        return GetBackingSpan(offset, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareAccessAfterGas(ulong newLength)
    {
        Debug.Assert(newLength <= Size);
        EnsureRented(newLength);
    }

    /// <summary>Prepares a range that will be completely overwritten after gas has been charged.</summary>
    /// <remarks>
    /// The caller must write every byte in the range before passing the result to
    /// <see cref="CommitOverwrite"/>. A zero result is a sentinel indicating that the range was
    /// already initialized and no commit is required. Preparation guarantees backing capacity only
    /// through the end of the overwrite; bytes above it remain lazy.
    /// </remarks>
    /// <param name="offset">The start of the overwrite range.</param>
    /// <param name="length">The length of the overwrite range.</param>
    /// <returns>The initialized prefix to commit, or zero when no commit is required.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong PrepareOverwriteAfterGas(ulong offset, ulong length)
        => PrepareOverwriteAfterGas(offset, length, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong PrepareOverwriteAfterGas(ulong offset, ulong length, ulong preservedEnd)
    {
        Debug.Assert(length != 0);
        Debug.Assert(offset + length <= Size);

        ulong overwriteEnd = offset + length;
        ulong initializedSize = _initializedSize;
        if (overwriteEnd > GetBackingCapacity() || offset > initializedSize)
        {
            return RentForOverwriteSlow(offset, overwriteEnd, preservedEnd);
        }

        return overwriteEnd > initializedSize ? overwriteEnd : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitOverwrite(ulong preparedInitializedSize)
    {
        if (preparedInitializedSize != 0)
        {
            _initializedSize = preparedInitializedSize;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureRented(ulong requiredEnd)
    {
        if (requiredEnd > GetBackingCapacity() || requiredEnd > _initializedSize)
        {
            RentSlow(requiredEnd);
        }
    }

    private const int MinRentSize = 1_024;
    // Above this, a cache miss rents from the shared pool instead of allocating (pow2 sizes from
    // here up are LOH-sized).
    private const int MaxNewAllocLength = 1 << 16;
    // Buffers up to this stay in the per-thread cache. Frames zero-extend their buffer on growth
    // (RentSlow), and a buffer that round-tripped through the shared pool between frames comes
    // back cold and coherence-invalidated under concurrent load — so those zeroing stores stall.
    // Keeping mid-size buffers on the renting thread keeps the lines warm; the byte budget bounds
    // per-thread retention.
    private const int MaxThreadCachedArrayLength = 1 << 18;
    private const int MaxThreadCachedBytes = 1 << 21;
    private const int CacheSlots = 16;

    private struct ThreadCache
    {
        public byte[]?[]? Arrays;
        public int Count;
        public int Bytes;
    }

    [ThreadStatic] private static ThreadCache _threadCache;

    // Explicit allocations are runtime-zeroed, while cached and pooled arrays are untrusted.
    // The initialized prefix lets the slow paths preserve that distinction through growth.
    private static byte[] Rent(int minLength, out ulong initializedSize)
    {
        ref ThreadCache threadCache = ref _threadCache;
        byte[]?[]? cache = threadCache.Arrays;
        int cachedArrayCount = threadCache.Count - 1;
        for (int i = cachedArrayCount; i >= 0; i--)
        {
            byte[] candidate = cache![i]!;
            if (candidate.Length >= minLength)
            {
                initializedSize = 0;
                threadCache.Count = cachedArrayCount;
                threadCache.Bytes -= candidate.Length;
                cache[i] = cache[cachedArrayCount];
                cache[cachedArrayCount] = null;
                return candidate;
            }
        }

        if (minLength > MaxNewAllocLength)
        {
            return RentLarge(minLength, out initializedSize);
        }

        byte[] fresh = new byte[ArrayPoolUtilities.GetPowerOfTwoCapacity(minLength)];
        initializedSize = (ulong)fresh.Length;
        return fresh;
    }

    private static void Return(byte[] array)
    {
        if (array.Length <= MaxThreadCachedArrayLength)
        {
            ref ThreadCache threadCache = ref _threadCache;
            if (threadCache.Count < CacheSlots
                && threadCache.Bytes + array.Length <= MaxThreadCachedBytes)
            {
                byte[]?[] cache = threadCache.Arrays ??= new byte[CacheSlots][];
                cache[threadCache.Count++] = array;
                threadCache.Bytes += array.Length;
                return;
            }
        }

        // Provenance: arrays <= MaxNewAllocLength are plain allocations, larger ones came from
        // RentLarge — an array must never reach a pool it was not rented from, so sub-threshold
        // arrays that miss the thread cache are dropped for the GC rather than pooled.
        if (array.Length > MaxNewAllocLength)
        {
            ReturnLarge(array);
        }
    }

#if ZK_EVM
    private static byte[] RentLarge(int minLength, out ulong initializedSize)
    {
        byte[] array = SafeArrayPool<byte>.Rent(minLength, out bool isFresh);
        initializedSize = isFresh ? (ulong)array.Length : 0;
        return array;
    }

    private static void ReturnLarge(byte[] array) => SafeArrayPool<byte>.Shared.Return(array);
#else
    private const int MaxSharedArrayLength = 1 << 20;
    // Buffers above this limit are allocated directly and are never returned to a pool.
    private const int MaxLargePooledArrayLength = 1 << 22;
    private static readonly System.Buffers.ArrayPool<byte> _largeArrayPool =
        System.Buffers.ArrayPool<byte>.Create(maxArrayLength: MaxLargePooledArrayLength, maxArraysPerBucket: 16);

    private static byte[] RentLarge(int minLength, out ulong initializedSize)
    {
        if (minLength > MaxLargePooledArrayLength)
        {
            byte[] fresh = new byte[ArrayPoolUtilities.GetPowerOfTwoCapacity(minLength)];
            initializedSize = (ulong)fresh.Length;
            return fresh;
        }

        initializedSize = 0;
        return minLength > MaxSharedArrayLength
            ? _largeArrayPool.Rent(minLength)
            : SafeArrayPool<byte>.Shared.Rent(minLength);
    }

    private static void ReturnLarge(byte[] array)
    {
        if (array.Length > MaxLargePooledArrayLength)
        {
            return;
        }

        if (array.Length > MaxSharedArrayLength)
            _largeArrayPool.Return(array);
        else
            SafeArrayPool<byte>.Shared.Return(array);
    }
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow(ulong requiredEnd)
    {
        ulong initializedSize = _initializedSize;
        if (_memory is null && _inlineMemoryManager is not null && requiredEnd <= InlineCapacity)
        {
            if (requiredEnd > initializedSize)
            {
                GetInlineSpan().Slice((int)initializedSize).Clear();
                _initializedSize = InlineCapacity;
            }

            return;
        }

        byte[] memory = EnsureCapacity(requiredEnd, initializedSize, ref initializedSize);

        if (requiredEnd > initializedSize)
        {
            // Over-zero to a chunk boundary so sequential MSTORE growth does not take RentSlow per word.
            const ulong zeroChunk = 4 * 1024;
            ulong target = Math.Min((ulong)memory.Length, (requiredEnd + (zeroChunk - 1)) & ~(zeroChunk - 1));
            Array.Clear(memory, (int)initializedSize, (int)(target - initializedSize));
            initializedSize = target;
        }

        _initializedSize = initializedSize;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong RentForOverwriteSlow(ulong overwriteStart, ulong overwriteEnd, ulong preservedEnd)
    {
        ulong initializedSize = _initializedSize;
        if (_memory is null && _inlineMemoryManager is not null && overwriteEnd <= InlineCapacity)
        {
            if (overwriteStart > initializedSize)
            {
                GetInlineSpan().Slice((int)initializedSize, (int)(overwriteStart - initializedSize)).Clear();
            }

            return Math.Max(initializedSize, overwriteEnd);
        }

        ulong preservedSize = Math.Min(initializedSize, Math.Max(overwriteStart, preservedEnd));
        byte[] memory = EnsureCapacity(overwriteEnd, preservedSize, ref initializedSize);

        if (overwriteStart > initializedSize)
        {
            Array.Clear(memory, (int)initializedSize, (int)(overwriteStart - initializedSize));
        }

        return Math.Max(initializedSize, overwriteEnd);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] EnsureCapacity(ulong minimumSize, ulong preservedSize, ref ulong initializedSize)
    {
        byte[]? memory = _memory;
        if (memory is null)
        {
            ReadOnlySpan<byte> preservedInline = _inlineMemoryManager is not null && preservedSize != 0
                ? GetInlineSpan().Slice(0, (int)preservedSize)
                : default;
            memory = Rent((int)Math.Max((uint)minimumSize, MinRentSize), out ulong rentedInitializedSize);
            if (_inlineMemoryManager is not null && preservedSize != 0)
            {
                preservedInline.CopyTo(memory);
            }

            _memory = memory;
            initializedSize = Math.Max(preservedSize, rentedInitializedSize);
        }
        else if (minimumSize > (ulong)memory.Length)
        {
            byte[] grown = Rent(TruncateToInt32(minimumSize), out ulong rentedInitializedSize);
            Array.Copy(memory, 0, grown, 0, (int)preservedSize);
            Return(memory);
            _memory = memory = grown;
            initializedSize = Math.Max(preservedSize, rentedInitializedSize);
        }

        return memory;
    }

    internal byte[]? BackingArray => _memory;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong GetBackingCapacity()
        => (ulong)(_memory?.Length ?? (_inlineMemoryManager is null ? 0 : InlineCapacity));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte GetBackingReference()
    {
        byte[]? memory = _memory;
        return ref memory is null
            ? ref MemoryMarshal.GetReference(GetInlineSpan())
            : ref MemoryMarshal.GetArrayDataReference(memory);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetBackingSpan(int offset, int length)
    {
        byte[]? memory = _memory;
        return memory is null
            ? GetInlineSpan().Slice(offset, length)
            : memory.AsSpan(offset, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetInlineSpan() => _inlineMemoryManager!.GetSpan();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlyMemory<byte> GetBackingMemory(int offset, int length)
    {
        byte[]? memory = _memory;
        return memory is null
            ? _inlineMemoryManager!.Memory.Slice(offset, length)
            : memory.AsMemory(offset, length);
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
