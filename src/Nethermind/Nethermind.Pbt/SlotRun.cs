// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.Pbt;

/// <summary>
/// The values of the <see cref="SlotRun.Width"/> consecutive storage slots that share a run key: an EIP-8297
/// storage key with its low four bits cleared. A run is whole — an absent slot is zero — and immutable.
/// </summary>
/// <remarks>
/// Instances are pooled by capacity (see <see cref="SlotRun"/>); a write produces a new run via
/// <see cref="With"/> and the caller returns the old one. The container holding a run owns it.
/// </remarks>
public interface ISlotRun
{
    /// <summary>The number of non-zero slots.</summary>
    int Count { get; }

    /// <summary>Bit <c>i</c> set when slot <c>i</c> is non-zero.</summary>
    ushort Mask { get; }

    /// <summary>The value of slot <paramref name="index"/>, zero when absent.</summary>
    EvmWord Get(int index);

    /// <summary>A new run with slot <paramref name="index"/> set to <paramref name="value"/> (zero clears it); this run is untouched.</summary>
    ISlotRun With(int index, in EvmWord value);

    /// <summary>A new run holding the same slots.</summary>
    ISlotRun Clone();

    /// <summary>The length of the persisted row <see cref="Encode"/> writes.</summary>
    int EncodedLength { get; }

    /// <summary>Writes the persisted row, <c>[log2(capacity)][mask u16 LE][32-byte value × Count, ascending slot]</c>, into the first <see cref="EncodedLength"/> bytes of <paramref name="destination"/>.</summary>
    /// <exception cref="InvalidOperationException">The run is empty; an empty run is a row deletion.</exception>
    void Encode(Span<byte> destination);
}

/// <summary>Creates, pools and keys <see cref="ISlotRun"/>s.</summary>
public static class SlotRun
{
    /// <summary>The slots one run key spans.</summary>
    public const int Width = 16;
    /// <summary>The type byte and mask that precede the values in a persisted row.</summary>
    public const int EncodedHeaderLength = 1 + sizeof(ushort);
    private const byte IndexMask = Width - 1;

    /// <summary>The shared all-zero run; never pooled.</summary>
    public static ISlotRun Empty { get; } = new EmptySlotRun();

    /// <summary>Rents the smallest run holding the slots set in <paramref name="mask"/>, taking their values from the <see cref="Width"/>-wide <paramref name="valuesByIndex"/>.</summary>
    public static ISlotRun Create(ushort mask, ReadOnlySpan<EvmWord> valuesByIndex)
    {
        int count = BitOperations.PopCount(mask);
        if (count == 0) return Empty;
        PackedSlotRun run = count switch
        {
            1 => StaticPool<SlotRun1>.Rent(),
            2 => StaticPool<SlotRun2>.Rent(),
            <= 4 => StaticPool<SlotRun4>.Rent(),
            <= 8 => StaticPool<SlotRun8>.Rent(),
            _ => StaticPool<SlotRun16>.Rent(),
        };
        run.Seed(mask, valuesByIndex);
        return run;
    }

    /// <summary>Returns <paramref name="run"/> to its pool; the caller must drop every reference to it.</summary>
    public static void Return(ISlotRun run) => ((PackedSlotRun)run).ReturnSelf();

    /// <summary>The key with its low four bits cleared: the key of the run holding <paramref name="slotKey"/>.</summary>
    public static TKey RunKey<TKey>(in TKey slotKey) where TKey : struct, IPbtKey<TKey> => WithLastByte(slotKey, (byte)(slotKey.Bytes[^1] & ~IndexMask));

    /// <summary>The slot's position within its run: the low four bits of the key.</summary>
    public static int IndexOf<TKey>(in TKey slotKey) where TKey : struct, IPbtKey<TKey> => slotKey.Bytes[^1] & IndexMask;

    /// <summary>The key of slot <paramref name="index"/> of the run keyed by <paramref name="runKey"/>.</summary>
    public static TKey SlotKey<TKey>(in TKey runKey, int index) where TKey : struct, IPbtKey<TKey> => WithLastByte(runKey, (byte)(runKey.Bytes[^1] | index));

    /// <summary>Whether <paramref name="key"/> has its low four bits cleared.</summary>
    public static bool IsRunKey<TKey>(in TKey key) where TKey : struct, IPbtKey<TKey> => (key.Bytes[^1] & IndexMask) == 0;

    /// <summary>The value of slot <paramref name="index"/> as a tree leaf value.</summary>
    public static ValueHash256 LeafValue(ISlotRun run, int index)
    {
        EvmWord value = run.Get(index);
        return new ValueHash256(EvmWordSlot.AsReadOnlySpan(in value));
    }

    private static TKey WithLastByte<TKey>(in TKey key, byte last) where TKey : struct, IPbtKey<TKey>
    {
        Span<byte> bytes = stackalloc byte[TKey.Capacity];
        key.Bytes.CopyTo(bytes);
        bytes[key.Length - 1] = last;
        return TKey.Create(bytes[..key.Length]);
    }
}

/// <summary>A run whose non-zero values are packed by rank into a fixed-capacity array.</summary>
internal abstract class PackedSlotRun(int capacity) : ISlotRun, IResettable
{
    private readonly EvmWord[] _values = new EvmWord[capacity];
    private ushort _mask;

    public int Count => BitOperations.PopCount(_mask);
    public ushort Mask => _mask;
    private ReadOnlySpan<EvmWord> PackedValues => _values.AsSpan(0, Count);

    public EvmWord Get(int index) => (_mask & (1 << index)) == 0 ? default : _values[Rank(index)];

    public ISlotRun With(int index, in EvmWord value)
    {
        Span<EvmWord> valuesByIndex = stackalloc EvmWord[SlotRun.Width];
        Expand(valuesByIndex);
        valuesByIndex[index] = value;
        int bit = 1 << index;
        return SlotRun.Create((ushort)(EvmWordSlot.IsZero(value) ? _mask & ~bit : _mask | bit), valuesByIndex);
    }

    public ISlotRun Clone()
    {
        Span<EvmWord> valuesByIndex = stackalloc EvmWord[SlotRun.Width];
        Expand(valuesByIndex);
        return SlotRun.Create(_mask, valuesByIndex);
    }

    public int EncodedLength => SlotRun.EncodedHeaderLength + Count * ValueHash256.MemorySize;

    public void Encode(Span<byte> destination)
    {
        if (Count == 0) throw new InvalidOperationException("An empty run is persisted as a row deletion.");
        destination[0] = (byte)BitOperations.Log2((uint)_values.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], _mask);
        MemoryMarshal.AsBytes(PackedValues).CopyTo(destination[SlotRun.EncodedHeaderLength..]);
    }

    internal void Seed(ushort mask, ReadOnlySpan<EvmWord> valuesByIndex)
    {
        _mask = mask;
        int rank = 0;
        for (int index = 0; index < SlotRun.Width; index++)
            if ((mask & (1 << index)) != 0) _values[rank++] = valuesByIndex[index];
    }

    private void Expand(Span<EvmWord> valuesByIndex)
    {
        valuesByIndex.Clear();
        int rank = 0;
        for (int index = 0; index < SlotRun.Width; index++)
            if ((_mask & (1 << index)) != 0) valuesByIndex[index] = _values[rank++];
    }

    private int Rank(int index) => BitOperations.PopCount((uint)(_mask & ((1 << index) - 1)));

    public void Reset() => _mask = 0;

    internal abstract void ReturnSelf();
}

internal sealed class EmptySlotRun() : PackedSlotRun(0)
{
    internal override void ReturnSelf() { }
}

internal sealed class SlotRun1() : PackedSlotRun(1)
{
    internal override void ReturnSelf() => StaticPool<SlotRun1>.Return(this);
}

internal sealed class SlotRun2() : PackedSlotRun(2)
{
    internal override void ReturnSelf() => StaticPool<SlotRun2>.Return(this);
}

internal sealed class SlotRun4() : PackedSlotRun(4)
{
    internal override void ReturnSelf() => StaticPool<SlotRun4>.Return(this);
}

internal sealed class SlotRun8() : PackedSlotRun(8)
{
    internal override void ReturnSelf() => StaticPool<SlotRun8>.Return(this);
}

internal sealed class SlotRun16() : PackedSlotRun(16)
{
    internal override void ReturnSelf() => StaticPool<SlotRun16>.Return(this);
}
