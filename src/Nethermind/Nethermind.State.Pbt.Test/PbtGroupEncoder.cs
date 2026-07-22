// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Writes a <see cref="PbtTrieNodeGroup"/> encoding one node at a time, at whatever positions the
/// caller names, for a test that needs a group of a given shape to hand <see cref="PbtTrieNodeGroup.Decode"/>.
/// </summary>
/// <remarks>
/// The production writer lives inside the updater's fold, which only ever emits the shapes a fold
/// produces — a stem at a boundary position, never an internal at the group root. The codec has to
/// read more than that: a stem wherever it lands, a root position occupied, a bitmap that does not
/// match the entries. This encoder exists to put those on the wire.
/// <para>
/// It takes the layout from <see cref="PbtTrieNodeGroup"/> itself — the entry lengths and the trailer
/// offsets — so only the order of the writes is restated here, and an encoding it produces that the
/// codec would not is caught by <see cref="PbtTrieNodeGroup.Decode"/> rather than passing quietly.
/// </para>
/// </remarks>
public ref struct PbtGroupEncoder(Span<byte> destination, PbtGroupFormat format)
{
    private readonly Span<byte> _destination = destination;
    private uint _presence;
    private uint _stems;
    private uint _chains;
    private int _offset = PbtTrieNodeGroup.EntriesOffset;

    /// <summary>Appends the internal node at <paramref name="position"/>, returning its entry offset.</summary>
    public int AppendInternal(int position, in ValueHash256 hash)
    {
        MarkPresent(position);
        return Write(hash.Bytes);
    }

    /// <summary>Appends the stem node at <paramref name="position"/>, returning its entry offset.</summary>
    public int AppendStem(int position, in Stem stem, in ValueHash256 leafSubtreeRoot)
    {
        MarkPresent(position);
        _stems |= 1u << position;
        int offset = Write(stem.Bytes);
        Write(leafSubtreeRoot.Bytes);
        return offset;
    }

    /// <summary>Appends the run at <paramref name="position"/>, a boundary slot's, whose whole encoding <paramref name="chain"/> is.</summary>
    public int AppendChain(int position, ReadOnlySpan<byte> chain)
    {
        MarkPresent(position);
        _chains |= 1u << PbtLayout.TrieNodeGroupBoundarySlot(position);
        return Write(chain);
    }

    /// <summary>Appends the trailer and returns the encoded length; 0 when nothing was appended.</summary>
    public readonly int Finish(in PbtSubtreeStats stats)
    {
        if (_presence == 0) return 0;

        Span<byte> trailer = _destination.Slice(_offset, PbtTrieNodeGroup.TrailerLength);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[PbtTrieNodeGroup.PresenceTrailerOffset..], _presence);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[PbtTrieNodeGroup.StemsTrailerOffset..], _stems);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[PbtTrieNodeGroup.ChainsTrailerOffset..], (ushort)_chains);
        stats.Write(trailer[PbtTrieNodeGroup.StatsTrailerOffset..]);
        trailer[PbtTrieNodeGroup.FormatTrailerOffset] = (byte)format;
        return _offset + PbtTrieNodeGroup.TrailerLength;
    }

    private void MarkPresent(int position)
    {
        Debug.Assert(_presence >> position == 0, "nodes must be appended in ascending position order");
        _presence |= 1u << position;
    }

    private int Write(ReadOnlySpan<byte> bytes)
    {
        int offset = _offset;
        bytes.CopyTo(_destination[offset..]);
        _offset = offset + bytes.Length;
        return offset;
    }
}
