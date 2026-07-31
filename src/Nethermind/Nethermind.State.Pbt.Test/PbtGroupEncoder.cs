// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

using Layout = Nethermind.Pbt.Tiles.PbtFourLevelTileLayout;
using Nethermind.Pbt.Tiles;

namespace Nethermind.State.Pbt.Test;

/// <summary>Encodes test node groups with caller-specified entries for <see cref="PbtTrieNodeGroup{TLayout}.Decode"/>.</summary>
/// <remarks>Supports malformed and otherwise unreachable group shapes needed to test decoder validation.</remarks>
public ref struct PbtGroupEncoder<TLayout>(Span<byte> destination, PbtGroupFormat format) where TLayout : IPbtTileLayout
{
    private readonly Span<byte> _destination = destination;
    private UInt128 _presence;
    private UInt128 _stems;
    private ulong _chains;
    private int _offset = PbtTrieNodeGroup.EntriesOffset;

    /// <summary>Appends an internal node and returns its entry offset.</summary>
    public int AppendInternal(int position, in ValueHash256 hash)
    {
        MarkPresent(position);
        return Write(hash.Bytes);
    }

    /// <summary>Appends a stem node and returns its entry offset.</summary>
    public int AppendStem(int position, in Stem stem, in ValueHash256 leafSubtreeRoot)
    {
        MarkPresent(position);
        _stems |= UInt128.One << position;
        int offset = Write(stem.Bytes);
        Write(leafSubtreeRoot.Bytes);
        return offset;
    }

    /// <summary>Appends a boundary-slot run and returns its entry offset.</summary>
    public int AppendChain(int position, ReadOnlySpan<byte> chain)
    {
        MarkPresent(position);
        _chains |= 1UL << PbtLayout.TrieNodeGroupBoundarySlot(position);
        return Write(chain);
    }

    /// <summary>Appends the trailer and returns the encoded length, or 0 for an empty group.</summary>
    public readonly int Finish(in PbtSubtreeStats stats)
    {
        if (_presence == 0) return 0;

        Span<byte> trailer = _destination.Slice(_offset, PbtTrieNodeGroup<TLayout>.TrailerLength);
        TLayout.WriteMasks(trailer, new NodeGroupBitmasks(_presence, _stems, _chains));
        stats.Write(trailer[PbtTrieNodeGroup<TLayout>.StatsTrailerOffset..]);
        trailer[PbtTrieNodeGroup<TLayout>.FormatTrailerOffset] = (byte)format;
        return _offset + PbtTrieNodeGroup<TLayout>.TrailerLength;
    }

    private void MarkPresent(int position)
    {
        Debug.Assert(_presence >> position == 0, "nodes must be appended in ascending position order");
        _presence |= UInt128.One << position;
    }

    private int Write(ReadOnlySpan<byte> bytes)
    {
        int offset = _offset;
        bytes.CopyTo(_destination[offset..]);
        _offset = offset + bytes.Length;
        return offset;
    }
}
