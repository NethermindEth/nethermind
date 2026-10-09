// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Snapshot;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt.Common;

/// <summary>Creates, pools and keys <see cref="PackedSlotRun"/>s.</summary>
public static class SlotRun
{
    /// <summary>The slots one run key spans.</summary>
    public const int Width = 16;
    private const byte IndexMask = Width - 1;
    private const int IndexBits = 4;

    /// <summary>The shared all-zero run; never pooled.</summary>
    public static PackedSlotRun Empty { get; } = new EmptySlotRun();

    /// <summary>Rents the smallest run holding the slots set in <paramref name="mask"/>, taking their values from the <see cref="Width"/>-wide <paramref name="valuesByIndex"/>.</summary>
    public static PackedSlotRun Create(ushort mask, ReadOnlySpan<EvmWord> valuesByIndex)
    {
        int count = BitOperations.PopCount(mask);
        if (count == 0) return Empty;
        PackedSlotRun run = Rent(count);
        run.Seed(mask, valuesByIndex);
        return run;
    }

    /// <summary>Rents the smallest run holding the slots set in <paramref name="mask"/>, taking their values packed by ascending slot from <paramref name="packedValues"/>.</summary>
    internal static PackedSlotRun CreatePacked(ushort mask, ReadOnlySpan<byte> packedValues)
    {
        int count = BitOperations.PopCount(mask);
        if (count == 0) return Empty;
        PackedSlotRun run = Rent(count);
        run.SeedPacked(mask, packedValues);
        return run;
    }

    private static PackedSlotRun Rent(int count) => count switch
    {
        1 => StaticPool<SlotRun1>.Rent(),
        2 => StaticPool<SlotRun2>.Rent(),
        <= 4 => StaticPool<SlotRun4>.Rent(),
        <= 8 => StaticPool<SlotRun8>.Rent(),
        _ => StaticPool<SlotRun16>.Rent(),
    };

    /// <summary>Returns <paramref name="run"/> to its pool; the caller must drop every reference to it.</summary>
    public static void Return(PackedSlotRun run) => run.ReturnSelf();

    /// <summary>The storage key with its low four bits cleared: the key of the run holding <paramref name="slotKey"/>.</summary>
    public static TKey RunKey<TKey>(in TKey slotKey) where TKey : struct, IPbtKey<TKey> => WithLastByte(slotKey, (byte)(slotKey.Bytes[^1] & ~IndexMask));

    /// <summary>The slot's position within its run: the low four bits of the storage key.</summary>
    public static int IndexOf<TKey>(in TKey slotKey) where TKey : struct, IPbtKey<TKey> => slotKey.Bytes[^1] & IndexMask;

    /// <summary>The position of storage slot <paramref name="slot"/> within its run: its low four bits.</summary>
    /// <remarks>A slot's storage key ends with the slot's low byte, offset by a multiple of <see cref="Width"/> for a header slot, so this is <see cref="IndexOf{TKey}"/> of that key.</remarks>
    internal static int IndexOf(in UInt256 slot) => (int)(slot.u0 & IndexMask);

    /// <summary>Whether storage slots <paramref name="slot"/> and <paramref name="other"/> of one address share a run.</summary>
    internal static bool InSameRun(in UInt256 slot, in UInt256 other) => slot >> IndexBits == other >> IndexBits;

    /// <summary>Picks <paramref name="header"/> for header-slot runs, keyed by <see cref="PbtPath"/>, and <paramref name="storage"/> for storage-zone runs, keyed by <see cref="PbtStoragePath"/>.</summary>
    internal static T ByZone<TKey, T>(object header, object storage) where TKey : struct, IPbtKey<TKey> where T : class =>
        (T)(typeof(TKey) == typeof(PbtPath) ? header
            : typeof(TKey) == typeof(PbtStoragePath) ? storage
            : throw new NotSupportedException($"Slot runs are keyed by {nameof(PbtPath)} or {nameof(PbtStoragePath)}, not {typeof(TKey).Name}."));

    private static TKey WithLastByte<TKey>(in TKey key, byte last) where TKey : struct, IPbtKey<TKey>
    {
        Span<byte> bytes = stackalloc byte[TKey.Capacity];
        key.Bytes.CopyTo(bytes);
        bytes[key.Length - 1] = last;
        return TKey.Create(bytes[..key.Length]);
    }
}

/// <summary>
/// The values of the <see cref="SlotRun.Width"/> consecutive storage slots that share a run key: an EIP-8297
/// storage key with its low four bits cleared. A run is whole — an absent slot is zero — and immutable.
/// </summary>
/// <remarks>
/// The non-zero values are packed by rank into a fixed-capacity array. Instances are pooled by capacity (see
/// <see cref="SlotRun"/>); a write produces a new run via <see cref="With"/> and the caller returns the old one.
/// The <see cref="PbtSnapshotContent"/> holding a run owns it.
/// </remarks>
public abstract class PackedSlotRun(int capacity) : IResettable
{
    private readonly EvmWord[] _values = new EvmWord[capacity];
    private ushort _mask;

    /// <summary>The number of non-zero slots.</summary>
    public int Count => BitOperations.PopCount(_mask);

    private ReadOnlySpan<EvmWord> PackedValues => _values.AsSpan(0, Count);

    /// <summary>The value of slot <paramref name="index"/>, zero when absent.</summary>
    public EvmWord Get(int index) => (_mask & (1 << index)) == 0 ? default : _values[Rank(index)];

    /// <summary>A new run with slot <paramref name="index"/> set to <paramref name="value"/> (zero clears it); this run is untouched.</summary>
    public PackedSlotRun With(int index, in EvmWord value)
    {
        Span<EvmWord> valuesByIndex = stackalloc EvmWord[SlotRun.Width];
        Expand(valuesByIndex);
        valuesByIndex[index] = value;
        int bit = 1 << index;
        return SlotRun.Create((ushort)(EvmWordSlot.IsZero(value) ? _mask & ~bit : _mask | bit), valuesByIndex);
    }

    /// <summary>A new run with <paramref name="writes"/>, all into slots of this run, applied in order (zero clears a slot); this run is untouched.</summary>
    internal PackedSlotRun With(ReadOnlySpan<SlotWrite> writes)
    {
        Span<EvmWord> valuesByIndex = stackalloc EvmWord[SlotRun.Width];
        Expand(valuesByIndex);
        int mask = _mask;
        foreach (SlotWrite write in writes)
        {
            int index = SlotRun.IndexOf(write.Slot);
            valuesByIndex[index] = write.Value;
            int bit = 1 << index;
            mask = EvmWordSlot.IsZero(write.Value) ? mask & ~bit : mask | bit;
        }
        return SlotRun.Create((ushort)mask, valuesByIndex);
    }

    /// <summary>A new run holding the same slots.</summary>
    public PackedSlotRun Clone() => SlotRun.CreatePacked(_mask, MemoryMarshal.AsBytes(PackedValues));

    /// <summary>The length of the persisted row <see cref="Encode"/> writes.</summary>
    public int EncodedLength => SlotRunCodec.HeaderLength + Count * ValueHash256.MemorySize;

    /// <summary>Writes the persisted row (see <see cref="Persistence.SlotRunCodec"/>) into the first <see cref="EncodedLength"/> bytes of <paramref name="destination"/>.</summary>
    public void Encode(Span<byte> destination)
    {
        destination[0] = (byte)BitOperations.Log2((uint)_values.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], _mask);
        MemoryMarshal.AsBytes(PackedValues).CopyTo(destination[SlotRunCodec.HeaderLength..]);
    }

    internal void Seed(ushort mask, ReadOnlySpan<EvmWord> valuesByIndex)
    {
        _mask = mask;
        int rank = 0;
        for (int index = 0; index < SlotRun.Width; index++)
            if ((mask & (1 << index)) != 0) _values[rank++] = valuesByIndex[index];
    }

    internal void SeedPacked(ushort mask, ReadOnlySpan<byte> packedValues)
    {
        _mask = mask;
        packedValues.CopyTo(MemoryMarshal.AsBytes(_values.AsSpan()));
    }

    private void Expand(Span<EvmWord> valuesByIndex)
    {
        valuesByIndex.Clear();
        int rank = 0;
        for (int index = 0; index < SlotRun.Width; index++)
            if ((_mask & (1 << index)) != 0) valuesByIndex[index] = _values[rank++];
    }

    private int Rank(int index) => BitOperations.PopCount((uint)(_mask & ((1 << index) - 1)));

    void IResettable.Reset() => _mask = 0;

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

/// <summary>A write of <paramref name="Value"/> into storage slot <paramref name="Slot"/>; a zero value clears the slot.</summary>
internal readonly record struct SlotWrite(UInt256 Slot, EvmWord Value);
