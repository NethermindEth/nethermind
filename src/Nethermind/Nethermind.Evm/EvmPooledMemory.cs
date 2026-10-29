// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

public partial struct EvmPooledMemory
{
    public const int WordSize = 32;
    // Matches the minimum rental tier, avoiding an earlier spill boundary for small frames.
    internal const int InlineCapacity = 1024;
    internal const ulong MaxMemorySize = int.MaxValue - WordSize + 1;
    internal const long MaxMemoryWords = (int.MaxValue - WordSize + 1L) / WordSize;

    // Bytes below this prefix are valid; Size may exceed both it and the backing array until a read
    // materializes the logical zero tail.
    private ulong _initializedSize;

    private EvmFrameMemory? _inlineMemoryManager;
    private byte[]? _memory;
    public ulong Size { get; private set; }

    /// <summary>Whether the memory keeps its bytes in the layout <see cref="SwizzledMemory"/> describes.</summary>
    /// <remarks>
    /// Then every byte this type takes or hands out is in natural order, converted at the boundary; only the references
    /// to the memory itself, which the guest's handlers read and write in place, see the layout. See
    /// <c>EvmPooledMemory.std.cs</c> and <c>.zkevm.cs</c>.
    /// </remarks>
    internal static partial bool IsSwizzled { get; }

    internal EvmPooledMemory(EvmFrameMemory inlineMemoryManager, bool isFresh = false)
    {
        _initializedSize = isFresh ? (ulong)InlineCapacity : 0;
        _inlineMemoryManager = inlineMemoryManager;
        inlineMemoryManager.SetBackingArray(null);
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
        if (IsSwizzled)
            SwizzledMemory.Write(ref Unsafe.Add(ref memory, offset), word);
        else
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref memory, offset), word1);
        return true;
    }

    public bool TrySaveByte(in UInt256 location, byte value)
    {
        CheckMemoryAccessViolation(in location, 1, out ulong newLength, out bool isViolation);
        if (isViolation) return false;

        int offset = TruncateToInt32(location.u0);
        UpdateSize(newLength);
        ref byte at = ref Unsafe.Add(ref GetBackingReference(), offset);
        (IsSwizzled ? ref SwizzledMemory.ByteAt(ref at) : ref at) = value;
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
        ulong preparedInitializedSize = PrepareOverwriteAfterGas(location.u0, (ulong)length, location.u0);
        if (IsSwizzled)
            SwizzledMemory.Write(ref MemoryMarshal.GetReference(GetBackingSpan(intLocation, length)), value);
        else
            Bytes.Copy(value, GetBackingSpan(intLocation, length));
        CommitOverwrite(preparedInitializedSize);
    }

    /// <summary>
    /// Copies a source range to memory and fills any bytes beyond the source with zeroes.
    /// The caller must have already charged for and expanded the destination range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <param name="sourceInMemoryLayout">Whether <paramref name="source"/> is laid out as memory, as a call's input is in
    /// swizzled memory (see <see cref="LoadInputAfterGas"/>), rather than in natural order.</param>
    internal void CopyFromZeroExtendedAfterGas(
        in UInt256 destination,
        ReadOnlySpan<byte> source,
        in UInt256 sourceOffset,
        int length,
        bool sourceInMemoryLayout = false)
    {
        if (length == 0)
        {
            return;
        }

        Debug.Assert(destination.IsUint64);
        Debug.Assert(destination.u0 + (ulong)length <= Size);
        ulong preparedInitializedSize = PrepareOverwriteAfterGas(destination.u0, (ulong)length, destination.u0);
        Span<byte> target = GetBackingSpan(TruncateToInt32(destination.u0), length);
        int copiedLength = 0;
        if (sourceOffset < source.Length)
        {
            int intSourceOffset = TruncateToInt32(sourceOffset.u0);
            copiedLength = Math.Min(source.Length - intSourceOffset, length);
            if (IsSwizzled && sourceInMemoryLayout)
                SwizzledMemory.Copy(ref MemoryMarshal.GetReference(target),
                    ref Unsafe.Add(ref MemoryMarshal.GetReference(source), intSourceOffset), copiedLength);
            else if (IsSwizzled)
                SwizzledMemory.Write(ref MemoryMarshal.GetReference(target), source.Slice(intSourceOffset, copiedLength));
            else
                Bytes.Copy(source.Slice(intSourceOffset, copiedLength), target);
        }

        if (copiedLength != length)
        {
            if (IsSwizzled)
                SwizzledMemory.Clear(ref Unsafe.Add(ref MemoryMarshal.GetReference(target), copiedLength), length - copiedLength);
            else
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

        data = Natural(GetBackingMemory(TruncateToInt32(location.u0), TruncateToInt32(length.u0)));
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

    [MethodImpl(MethodImplOptions.NoInlining)]
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
        return Natural(GetBackingMemory((int)location, (int)length));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
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

    /// <summary>Stores a native stack word as big-endian bytes after memory expansion gas has been charged.</summary>
    /// <remarks>The source must not alias this memory instance, which can replace its buffer during expansion.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreNativeWordAfterGas(in UInt256 location, ReadOnlySpan<byte> word)
    {
        Debug.Assert(location.IsUint64);
        ulong overwriteEnd = location.u0 + WordSize;
        ulong initializedSize = _initializedSize;
        ulong preparedInitializedSize = 0;
        if (overwriteEnd > initializedSize)
        {
            if (location.u0 <= initializedSize && overwriteEnd <= GetBackingCapacity())
            {
                preparedInitializedSize = overwriteEnd;
            }
            else if (_memory is null && _inlineMemoryManager is not null && overwriteEnd <= InlineCapacity)
            {
                GetInlineSpan().Slice((int)initializedSize, (int)(GroupEnd(location.u0) - initializedSize)).Clear();
                preparedInitializedSize = overwriteEnd;
            }
            else
            {
                MaterializeArray(overwriteEnd);
            }
        }
        ref byte destination = ref Unsafe.Add(ref GetBackingReference(), TruncateToInt32(location.u0));
        ref byte source = ref MemoryMarshal.GetReference(word);
        if (IsSwizzled)
        {
            SwizzledMemory.StoreNativeWord(ref destination, ref source);
            CommitOverwrite(preparedInitializedSize);
            return;
        }

        Bytes.Bswap64Hoist swap = Bytes.HoistBswap64();
        Unsafe.WriteUnaligned(ref destination, swap.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 24))));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 8), swap.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 16))));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), swap.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 8))));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 24), swap.Bswap64(Unsafe.ReadUnaligned<ulong>(ref source)));
        CommitOverwrite(preparedInitializedSize);
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
        if (IsSwizzled)
        {
            SaveAfterGas(in location, word);
            return;
        }

        int offset = TruncateToInt32(location.u0);
        ulong overwriteEnd = location.u0 + WordSize;
        byte[]? memory = _memory;
        if (memory is not null)
        {
            ulong initializedSize = _initializedSize;
            if (overwriteEnd <= initializedSize)
            {
                WriteWord(memory, offset, word);
                return;
            }

            if (location.u0 <= initializedSize && overwriteEnd <= (ulong)memory.Length)
            {
                WriteWord(memory, offset, word);
                _initializedSize = overwriteEnd;
                return;
            }
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
        MaterializeArray(overwriteEnd);
        WriteWord(_memory!, offset, word);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteWord(byte[] memory, int offset, ReadOnlySpan<byte> word)
    {
        EvmWord value = Unsafe.As<byte, EvmWord>(ref MemoryMarshal.GetReference(word));
        ref byte memoryData = ref MemoryMarshal.GetArrayDataReference(memory);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref memoryData, offset), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreByteAfterGas(in UInt256 location, byte value)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        ulong overwriteEnd = location.u0 + 1;
        if (IsSwizzled)
        {
            PrepareAccessAfterGas(overwriteEnd);
            SwizzledMemory.ByteAt(ref Unsafe.Add(ref GetBackingReference(), offset)) = value;
            return;
        }

        byte[]? memory = _memory;
        if (memory is not null && overwriteEnd <= _initializedSize)
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
        ref byte word = ref Unsafe.Add(ref GetBackingReference(), offset);
        if (!IsSwizzled) return ref word;

        // The word in natural order, in a buffer of its own: the guest runs one frame at a time.
        byte[] natural = s_naturalWord ??= new byte[WordSize];
        SwizzledMemory.Read(ref word, natural);
        return ref MemoryMarshal.GetArrayDataReference(natural);
    }

    private static byte[]? s_naturalWord;

    /// <summary>
    /// Returns the 32 bytes at <paramref name="offset"/> when they lie inside both the active and the initialized memory,
    /// or a null reference when they do not.
    /// </summary>
    /// <param name="offset">
    /// The start of the word, which the caller must keep at most <see cref="uint.MaxValue"/>: the bound is asserted only
    /// in debug builds, and a larger offset wraps the bounds check and reaches outside the memory.
    /// </param>
    /// <remarks>
    /// A word there needs no expansion gas and no initialization, so reading it in place is the whole access.
    /// The same caveat as <see cref="Load32BytesAfterGas"/> applies to the returned ref.
    /// </remarks>
    [Experimental("NMEVM0001")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte GetActiveInitializedWord(ulong offset)
    {
        Debug.Assert(offset <= uint.MaxValue);
        ulong end = offset + WordSize;
        if (end > Size || end > _initializedSize) return ref Unsafe.NullRef<byte>();
        return ref Unsafe.Add(ref GetBackingReference(), (nint)offset);
    }

    /// <summary>
    /// The expansion gas an overwrite of the 32 bytes at <paramref name="offset"/> needs when they need no clearing and
    /// no new backing, or <see cref="ulong.MaxValue"/>, which no gas covers, when they do.
    /// </summary>
    /// <param name="offset">
    /// The start of the word, which the caller must keep at most <see cref="uint.MaxValue"/>: the bound is asserted only
    /// in debug builds, and a larger offset wraps the bounds check and reaches outside the memory.
    /// </param>
    /// <remarks>
    /// A word that starts inside the initialized memory leaves no gap to clear below it, so it may extend the
    /// initialized memory up to the backing's capacity, as <see cref="StoreWordAfterGas"/> lets it. Nothing changes;
    /// <see cref="CommitWordOverwrite"/> does that once the cost has been charged.
    /// </remarks>
    [Experimental("NMEVM0001")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetWordOverwriteCost(ulong offset)
    {
        Debug.Assert(offset <= uint.MaxValue);
        ulong end = offset + WordSize;
        ulong initializedSize = _initializedSize;
        if ((end > initializedSize && (offset > initializedSize || end > GetBackingCapacity())) ||
            (IsSwizzled && (offset & GroupMask) != 0))
            return ulong.MaxValue;

        Debug.Assert(end <= MaxMemorySize, "The backing never reaches past the largest addressable size.");
        ulong size = Size;
        return end > size ? ExpansionCost(size >> 5, (end + (WordSize - 1UL)) >> 5) : 0;
    }

    /// <summary>
    /// Makes the 32 bytes at <paramref name="offset"/> active and initialized, and returns them to be overwritten.
    /// </summary>
    /// <param name="offset">
    /// The start of the word, at most <see cref="uint.MaxValue"/>, whose <see cref="GetWordOverwriteCost"/> has been charged.
    /// </param>
    /// <remarks>
    /// The caller must write all 32 bytes. The same caveat as <see cref="Load32BytesAfterGas"/> applies to the returned ref.
    /// </remarks>
    [Experimental("NMEVM0001")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte CommitWordOverwrite(ulong offset)
    {
        ulong end = offset + WordSize;
        if (end > Size) Size = (end + (WordSize - 1UL)) & ~(WordSize - 1UL);
        if (end > _initializedSize) _initializedSize = end;
        // The offset rederived rather than held through the updates, where it would take a callee-saved register.
        return ref Unsafe.Add(ref GetBackingReference(), (nint)(end - WordSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> LoadSpanAfterGas(in UInt256 location, ulong length)
    {
        Debug.Assert(location.IsUint64);
        int offset = TruncateToInt32(location.u0);
        int intLength = TruncateToInt32(length);
        PrepareAccessAfterGas(location.u0 + length);
        return Natural(GetBackingSpan(offset, intLength));
    }

    /// <summary>
    /// Variant of <see cref="TryLoadSpan(in UInt256, in UInt256, out Span{byte})"/> requiring the caller to have
    /// already charged memory expansion for exactly this range; a zero length yields an empty span at any location.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> LoadSpanAfterGas(in UInt256 location, in UInt256 length)
    {
        if (length.IsZero)
        {
            return [];
        }

        Debug.Assert(length.IsUint64);
        return LoadSpanAfterGas(in location, length.u0);
    }

    /// <summary><see cref="LoadSpanAfterGas(in UInt256, in UInt256)"/> as the memory holds the bytes: swizzled where
    /// <see cref="IsSwizzled"/>, for a caller that converts them as it copies them out.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> LoadLaidOutSpanAfterGas(in UInt256 location, in UInt256 length)
    {
        if (length.IsZero)
        {
            return [];
        }

        Debug.Assert(location.IsUint64 && length.IsUint64);
        PrepareAccessAfterGas(location.u0 + length.u0);
        return GetBackingSpan(TruncateToInt32(location.u0), TruncateToInt32(length.u0));
    }

    /// <summary>
    /// Variant of <see cref="TryLoad"/> requiring the caller to have already charged memory expansion for exactly
    /// this range; a zero length yields an empty result at any location.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlyMemory<byte> LoadAfterGas(in UInt256 location, in UInt256 length)
    {
        if (length.IsZero)
        {
            return default;
        }

        Debug.Assert(location.IsUint64);
        Debug.Assert(length.IsUint64);
        PrepareAccessAfterGas(location.u0 + length.u0);
        return Natural(GetBackingMemory(TruncateToInt32(location.u0), TruncateToInt32(length.u0)));
    }

    /// <summary><see cref="LoadAfterGas"/> for the input of a call, in the layout a call's input takes.</summary>
    /// <remarks>
    /// In swizzled memory the input keeps the memory's layout, laid out from its own start: a range starting on a group
    /// boundary is handed over as it lies, without a copy, and any other is laid out again in a new array.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlyMemory<byte> LoadInputAfterGas(in UInt256 location, in UInt256 length)
    {
        if (!IsSwizzled) return LoadAfterGas(in location, in length);
        if (length.IsZero) return default;

        PrepareAccessAfterGas(location.u0 + length.u0);
        ReadOnlyMemory<byte> input = GetBackingMemory(TruncateToInt32(location.u0), TruncateToInt32(length.u0));
        return (location.u0 & GroupMask) == 0 ? input : SwizzledMemory.FromNatural(SwizzledMemory.ToNatural(input).Span);
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
        if (IsSwizzled)
        {
            CopySwizzledAfterGas(in destination, in source, length, sourceEnd);
            return;
        }

        ulong destinationEnd = destination.u0 + length;
        ulong initializedSize = _initializedSize;
        if (sourceEnd <= initializedSize
            && destinationEnd <= GetBackingCapacity()
            && destination.u0 <= initializedSize)
        {
            int intLength = TruncateToInt32(length);
            GetBackingSpan(TruncateToInt32(source.u0), intLength)
                .CopyTo(GetBackingSpan(TruncateToInt32(destination.u0), intLength));
            if (destinationEnd > initializedSize)
            {
                _initializedSize = destinationEnd;
            }

            return;
        }

        CopyAfterGasSlow(in destination, in source, length, sourceEnd);
    }

    /// <summary><see cref="CopyAfterGas"/> in swizzled memory.</summary>
    /// <remarks>Initializes the source first, so its unwritten bytes read as the zeroes they stand for.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CopySwizzledAfterGas(in UInt256 destination, in UInt256 source, ulong length, ulong sourceEnd)
    {
        PrepareAccessAfterGas(sourceEnd);
        ulong preparedInitializedSize = PrepareOverwriteAfterGas(destination.u0, length, sourceEnd);
        ref byte memory = ref GetBackingReference();
        SwizzledMemory.Move(
            ref Unsafe.Add(ref memory, TruncateToInt32(destination.u0)),
            ref Unsafe.Add(ref memory, TruncateToInt32(source.u0)),
            TruncateToInt32(length));
        CommitOverwrite(preparedInitializedSize);
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
        Bytes.Copy(GetBackingSpan(TruncateToInt32(source.u0), intLength), target);
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
        return ExpansionCost(activeWords, newActiveWords);
    }

    /// <summary>The Yellow Paper memory cost of growing from <paramref name="activeWords"/> to <paramref name="newActiveWords"/> words.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ExpansionCost(ulong activeWords, ulong newActiveWords) =>
        (newActiveWords - activeWords) * GasCostOf.Memory +
        ((newActiveWords * newActiveWords) >> 9) -
        ((activeWords * activeWords) >> 9);

    [MethodImpl(MethodImplOptions.NoInlining)]
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
        return new(size, Natural(GetBackingMemory(0, visible)));
    }

    public void Dispose()
    {
        byte[]? memory = _memory;

        if (memory is not null)
        {
            _memory = null;
            Return(memory);
        }

        _inlineMemoryManager?.SetBackingArray(null);

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
        return Natural(GetBackingSpan(offset, length));
    }

    /// <summary><paramref name="span"/> of the memory as it lies, or a copy of its bytes in natural order where the memory
    /// is swizzled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Span<byte> Natural(Span<byte> span) =>
        IsSwizzled ? SwizzledMemory.ToArray(ref MemoryMarshal.GetReference(span), span.Length) : span;

    /// <inheritdoc cref="Natural(Span{byte})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlyMemory<byte> Natural(ReadOnlyMemory<byte> memory) =>
        IsSwizzled ? SwizzledMemory.ToArray(ref MemoryMarshal.GetReference(memory.Span), memory.Length) : memory;

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
            _initializedSize = CompleteGroup(preparedInitializedSize);
        }
    }

    /// <summary>The end the initialized prefix takes for one ending at <paramref name="end"/>.</summary>
    /// <remarks>
    /// In swizzled memory the next group boundary, the rest of the group zeroed: every clear starts where the prefix ends,
    /// and one starting inside a group would clear bytes the prefix holds and miss bytes it should clear.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong CompleteGroup(ulong end)
    {
        if (!IsSwizzled || (end & GroupMask) == 0) return end;

        ulong groupEnd = GroupEnd(end);
        ref byte memory = ref GetBackingReference();
        for (ulong at = end; at < groupEnd; at++)
            SwizzledMemory.ByteAt(ref Unsafe.Add(ref memory, (nint)at)) = 0;
        return groupEnd;
    }

    /// <summary><paramref name="offset"/> rounded up to a group boundary in swizzled memory, as a clear up to it must be.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GroupEnd(ulong offset) => IsSwizzled ? (offset + GroupMask) & ~GroupMask : offset;

    private const ulong GroupMask = sizeof(uint) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureRented(ulong requiredEnd)
    {
        Debug.Assert(_initializedSize <= GetBackingCapacity());
        if (requiredEnd > _initializedSize)
        {
            RentSlow(requiredEnd);
        }
    }

    private const int MinRentSize = 1_024;
    private const int InlineZeroChunk = 256;
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
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cache), i) = cache[cachedArrayCount]; // cache[i] = array, but avoiding stelemref
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
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cache), threadCache.Count++) = array; // cache[i] = array, but avoiding stelemref
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


    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow(ulong requiredEnd)
    {
        if (_memory is null && _inlineMemoryManager is not null && requiredEnd <= InlineCapacity)
        {
            ulong initializedSize = _initializedSize;
            if (requiredEnd > initializedSize)
            {
                // Zero to the next chunk boundary rather than the whole inline tier, so a spill copies only
                // the prefix the frame touched; InlineCapacity is a multiple of the chunk.
                ulong target = (requiredEnd + (InlineZeroChunk - 1)) & ~(InlineZeroChunk - 1UL);
                GetInlineSpan().Slice((int)initializedSize, (int)(target - initializedSize)).Clear();
                _initializedSize = target;
            }

            return;
        }

        MaterializeArray(requiredEnd);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MaterializeArray(ulong requiredEnd)
    {
        ulong initializedSize = _initializedSize;
        byte[] memory = EnsureCapacity(requiredEnd, initializedSize, ref initializedSize);

        if (requiredEnd > initializedSize)
        {
            // Over-zero to a chunk boundary so sequential MSTORE growth stays amortized; size the window from
            // requiredEnd rather than the expansion delta to limit wasted clearing for small frames.
            // requiredEnd fits in the backing array. Shifting 61 set bits by clz(end - 1) gives chunk - 1;
            // forcing bit 10 and masking to 12 bits implements the 256–4096 byte clamp.
            ulong value = (requiredEnd - 1) | 1024UL;
            ulong zeroMask = (0x1fff_ffff_ffff_ffffUL >> Bytes.LeadingZeroBits(value)) & 0xfffUL;
            ulong target = Math.Min((ulong)memory.Length, (requiredEnd + zeroMask) & ~zeroMask);
            Array.Clear(memory, (int)initializedSize, (int)(target - initializedSize));
            initializedSize = target;
        }

        _initializedSize = initializedSize;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong RentForOverwriteSlow(ulong overwriteStart, ulong overwriteEnd, ulong preservedEnd)
    {
        // Bulk callers overwrite the entire range, so only its preceding gap needs materializing;
        // incremental accesses use chunked over-zeroing instead.
        ulong initializedSize = _initializedSize;
        if (_memory is null && _inlineMemoryManager is not null && overwriteEnd <= InlineCapacity)
        {
            if (overwriteStart > initializedSize)
            {
                GetInlineSpan().Slice((int)initializedSize, (int)(GroupEnd(overwriteStart) - initializedSize)).Clear();
            }

            return Math.Max(initializedSize, overwriteEnd);
        }

        ulong preservedSize = GroupEnd(Math.Min(initializedSize, Math.Max(overwriteStart, preservedEnd)));
        byte[] memory = EnsureCapacity(overwriteEnd, preservedSize, ref initializedSize);

        if (overwriteStart > initializedSize)
        {
            Array.Clear(memory, (int)initializedSize, (int)(GroupEnd(overwriteStart) - initializedSize));
        }

        return Math.Max(initializedSize, overwriteEnd);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] EnsureCapacity(ulong minimumSize, ulong preservedSize, ref ulong initializedSize)
    {
        byte[]? memory = _memory;
        if (memory is null)
        {
            InitializeCapacity(minimumSize, preservedSize, out initializedSize, out memory);
        }

        else if (minimumSize > (ulong)memory.Length)
        {
            memory = GrowCapacitySlow(memory, minimumSize, preservedSize, ref initializedSize);
        }

        return memory;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InitializeCapacity(ulong minimumSize, ulong preservedSize, out ulong initializedSize, out byte[] memory)
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
        _inlineMemoryManager?.SetBackingArray(memory);
        initializedSize = Math.Max(preservedSize, rentedInitializedSize);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte[] GrowCapacitySlow(
        byte[] memory,
        ulong minimumSize,
        ulong preservedSize,
        ref ulong initializedSize)
    {
        byte[] grown = Rent(TruncateToInt32(minimumSize), out ulong rentedInitializedSize);
        Array.Copy(memory, 0, grown, 0, (int)preservedSize);
        Return(memory);
        _memory = grown;
        _inlineMemoryManager?.SetBackingArray(grown);
        initializedSize = Math.Max(preservedSize, rentedInitializedSize);
        return grown;
    }

    internal byte[]? BackingArray => _memory ?? _inlineMemoryManager?.BackingArray;

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
