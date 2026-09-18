// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Caching;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// The values of the <see cref="SlotRun.Width"/> consecutive storage slots that share a run key: an EIP-8297
/// storage key with its low four bits cleared. A run is whole — an absent slot is zero — and immutable.
/// </summary>
/// <remarks>
/// Instances are pooled by capacity (see <see cref="SlotRun"/>); a write produces a new run via
/// <see cref="With"/> and the caller returns the old one. The <see cref="PbtSnapshotContent"/> holding a
/// run owns it.
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
}

/// <summary>Creates, pools and keys <see cref="ISlotRun"/>s.</summary>
public static class SlotRun
{
    public const int Width = 16;
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

    public static PbtStorageTreeKey RunKey(in PbtStorageTreeKey slotKey) => WithLastByte(slotKey, (byte)(slotKey.Bytes[^1] & ~IndexMask));

    public static int IndexOf(in PbtStorageTreeKey slotKey) => slotKey.Bytes[^1] & IndexMask;

    public static PbtStorageTreeKey SlotKey(in PbtStorageTreeKey runKey, int index) => WithLastByte(runKey, (byte)(runKey.Bytes[^1] | index));

    public static bool IsRunKey(in PbtStorageTreeKey key) => (key.Bytes[^1] & IndexMask) == 0;

    private static PbtStorageTreeKey WithLastByte(in PbtStorageTreeKey key, byte last)
    {
        Span<byte> bytes = stackalloc byte[PbtStorageTreeKey.MaxLength];
        key.Bytes.CopyTo(bytes);
        bytes[key.Length - 1] = last;
        return new PbtStorageTreeKey(bytes[..key.Length]);
    }
}

/// <summary>A run whose non-zero values are packed by rank into a fixed-capacity array.</summary>
internal abstract class PackedSlotRun(int capacity) : ISlotRun, IResettable
{
    private readonly EvmWord[] _values = new EvmWord[capacity];
    private ushort _mask;

    public int Count => BitOperations.PopCount(_mask);
    public ushort Mask => _mask;
    internal int Capacity => _values.Length;
    internal ReadOnlySpan<EvmWord> PackedValues => _values.AsSpan(0, Count);

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
