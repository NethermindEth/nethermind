// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt.Tiles;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.Pbt;

/// <summary>
/// A non-owning, copyable view of bytes the trie descent reads. The owner of <see cref="Memory"/> keeps
/// its lease for the lifetime of the view; a view handed to another thread must be leased explicitly.
/// </summary>
internal readonly struct TreeReader<TLayout> where TLayout : IPbtTileLayout
{
    private readonly RefCountingMemory? _memory;
    private readonly int _offset;
    private readonly int _length;
    private readonly NodeKind _kind;
    private readonly bool _hasStoredEncoding;
    private readonly int _seedSlotPlusOne;

    private TreeReader(
        RefCountingMemory? memory, int offset, int length, NodeKind kind = NodeKind.Absent,
        bool hasStoredEncoding = false, int seedSlot = -1)
    {
        _memory = memory;
        _offset = offset;
        _length = length;
        _kind = kind;
        _hasStoredEncoding = hasStoredEncoding;
        _seedSlotPlusOne = seedSlot + 1;
    }

    /// <summary>The whole of <paramref name="memory"/>, as the store hands a blob over; the absent reader for <c>null</c>.</summary>
    public static TreeReader<TLayout> Of(RefCountingMemory? memory) =>
        memory is null ? default : new(memory, 0, memory.GetSpan().Length);

    public bool IsEmpty => _memory is null;

    public bool HasStoredEncoding => _hasStoredEncoding;

    public RefCountingMemory? Memory => _memory;

    public int SeedSlot => _seedSlotPlusOne - 1;

    public ReadOnlySpan<byte> Data => _memory is null ? default : _memory.GetSpan().Slice(_offset, _length);

    public Occupant Occupant => _kind == NodeKind.Absent ? default : new(Data, _kind);

    public TreeReader<TLayout> AsGroup() => new(_memory, _offset, _length, hasStoredEncoding: !IsEmpty);

    public TreeReader<TLayout> WithSeed(int slot) => new(_memory, _offset, _length, _kind, seedSlot: slot);

    public PbtTrieNodeGroup<TLayout> Group() =>
        _hasStoredEncoding ? PbtTrieNodeGroup<TLayout>.Decode(Data) : default;

    public BoundarySlotMasks<TLayout> BoundaryShape()
    {
        if (_hasStoredEncoding) return Group().BoundaryShape();
        if (_seedSlotPlusOne == 0) return default;

        SlotBitmask<TLayout> occupied = SlotBitmask<TLayout>.Of(_seedSlotPlusOne - 1);
        return new BoundarySlotMasks<TLayout>(
            occupied,
            _kind == NodeKind.Stem ? occupied : default,
            _kind == NodeKind.Chain ? occupied : default);
    }

    public TreeReader<TLayout> Reader(int slot, in PbtTrieNodeGroup<TLayout> group)
    {
        if (!_hasStoredEncoding) return slot + 1 == _seedSlotPlusOne ? AsNode(_kind) : default;

        int position = PbtLayout.TrieNodeGroupBoundarySlotPosition(slot);
        NodeKind kind = group.KindAt(position);
        return kind == NodeKind.Absent
            ? default
            : Slice(group.EntryOffset(position)..).AsNode(kind);
    }

    /// <summary>The reader <paramref name="range"/> of these bytes holds; the absent reader for an empty range.</summary>
    private TreeReader<TLayout> Slice(Range range)
    {
        (int start, int sliceLength) = range.GetOffsetAndLength(_length);
        return sliceLength == 0 ? default : new TreeReader<TLayout>(_memory, _offset + start, sliceLength);
    }

    public TreeReader<TLayout> AsNode(NodeKind nodeKind)
    {
        int nodeLength = new Occupant(Data, nodeKind).Encoding.Length;
        return new TreeReader<TLayout>(_memory, _offset, nodeLength, nodeKind);
    }

    public TreeReader<TLayout> Lease()
    {
        _memory?.AcquireLease();
        return this;
    }
}

/// <summary>
/// A boundary node's encoding as the trie descent reads it. The span borrows its reader's memory and
/// must not outlive that read.
/// </summary>
internal readonly ref struct Occupant(ReadOnlySpan<byte> data, NodeKind kind)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private readonly NodeKind _kind = kind;

    public NodeKind Kind => _kind;

    /// <summary>This node's exact encoding, borrowed from the memory holding it.</summary>
    public ReadOnlySpan<byte> Encoding => Slot.Encoding;

    /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Stem"/>
    public Stem Stem => Slot.Stem;

    /// <inheritdoc cref="PbtTrieNodeGroup.Slot.ChainData"/>
    public ReadOnlySpan<byte> ChainData => Slot.ChainData;

    /// <inheritdoc cref="PbtTrieNodeGroup.Slot.Hash"/>
    public ValueHash256 Hash => Slot.Hash;

    /// <inheritdoc cref="PbtTrieNodeGroup.Slot.NodeHash"/>
    public ValueHash256 NodeHash() => Slot.NodeHash();

    private PbtTrieNodeGroup.Slot Slot => PbtTrieNodeGroup.SlotAt(_data, _kind);
}
