// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

public readonly ref partial struct PbtTrieNodeGroup
{
    /// <summary>
    /// Encodes a group into a caller-supplied buffer one node at a time, as the producer walks the
    /// group, rather than from a materialized set of slots.
    /// </summary>
    /// <remarks>
    /// Nodes must be appended in ascending position order — the order a post-order walk of the group
    /// visits them, and the order the encoding stores them in — which makes each entry's offset
    /// implicit in the append cursor: no offset math is needed while building. An appended entry keeps
    /// a stable offset, so a producer can read a node back through <see cref="SlotAt"/> instead of
    /// carrying hashes up its walk by value.
    /// <para>
    /// The bitmaps are only known once the walk is done, which is why they sit in the trailer rather
    /// than at the front: <see cref="Finish"/> appends them where the entries ended, leaving the whole
    /// encoding a single forward pass. Backfilling a leading header instead would write back over
    /// bytes the write has already streamed past, which no prefetcher helps with. The format byte
    /// rides along with them so that an encoding ends with its own identifier and its entries start
    /// at offset zero, which is what lets a container hold one verbatim.
    /// </para>
    /// </remarks>
    public ref struct Builder
    {
        private readonly Span<byte> _destination;
        private readonly PbtGroupFormat _format;
        private uint _presence;
        private uint _stems;
        private uint _chains;
        private int _offset = EntriesOffset;

        public Builder(Span<byte> destination, PbtGroupFormat format)
        {
            _destination = destination;
            _format = format;
        }

        /// <summary>Appends the internal node at <paramref name="position"/>, returning its entry offset.</summary>
        public int AppendInternal(int position, in ValueHash256 hash)
        {
            Debug.Assert(hash != default, "internal nodes never cache a zero hash");
            Debug.Assert(!IsSkippedPosition(_format, position), "an interleaved group stores no internal node at a skipped level");
            MarkPresent(position);
            return Write(hash.Bytes);
        }

        /// <summary>Appends the stem node at <paramref name="position"/>, returning its entry offset.</summary>
        public int AppendStem(int position, in Stem stem, in ValueHash256 leafSubtreeRoot)
        {
            MarkPresent(position);
            _stems |= 1u << position;
            int offset = _offset;
            Span<byte> entry = _destination[offset..];
            stem.Bytes.CopyTo(entry);
            leafSubtreeRoot.Bytes.CopyTo(entry[Stem.Length..]);
            _offset = offset + Slot.StemLength;
            return offset;
        }

        /// <summary>Appends the run at <paramref name="position"/>, a boundary slot's, whose whole encoding <paramref name="chain"/> is, returning its entry offset.</summary>
        public int AppendChain(int position, ReadOnlySpan<byte> chain)
        {
            Debug.Assert(IsBoundaryPosition(position), "a run hangs from a boundary slot and nowhere else");
            Debug.Assert(chain.Length == Slot.ChainLength);
            MarkPresent(position);
            _chains |= 1u << BoundarySlot(position);
            return Write(chain);
        }

        /// <summary>
        /// Appends a whole subtree at once — <paramref name="entries"/>, holding the nodes
        /// <paramref name="masks"/> describes — returning its root's entry offset.
        /// </summary>
        /// <remarks>
        /// The run is another group's encoding of that same subtree (<see cref="SubtreeEntries"/>), which
        /// is byte-for-byte what appending its nodes one at a time would produce, so a producer that finds
        /// a subtree unchanged copies it rather than folding it. Its root is its highest position, hence
        /// the last entry of the run.
        /// </remarks>
        public int AppendSubtree(NodeGroupBitmasks masks, ReadOnlySpan<byte> entries)
        {
            (uint presence, uint stems, uint chains) = masks;
            Debug.Assert(presence != 0, "an absent subtree has no entries to append");
            Debug.Assert((stems & ~presence) == 0, "only a present position holds a stem");
            Debug.Assert(_presence >> BitOperations.TrailingZeroCount(presence) == 0, "nodes must be appended in ascending position order");
            Debug.Assert(
                _format == PbtGroupFormat.EveryLevel || (presence & SkippedPositionsMask & ~stems) == 0,
                "a verbatim run must be in this group's own format");

            _presence |= presence;
            _stems |= stems;
            _chains |= chains;

            int rootOffset = _offset + entries.Length - RootEntryLength(BitOperations.Log2(presence), masks);
            Write(entries);
            return rootOffset;
        }

        /// <summary>The length of the entry at <paramref name="rootPosition"/>, the last of an appended subtree's.</summary>
        private static int RootEntryLength(int rootPosition, NodeGroupBitmasks masks) =>
            (masks.Stems >> rootPosition & 1) != 0 ? Slot.StemLength
            : IsBoundaryPosition(rootPosition) && (masks.Chains >> BoundarySlot(rootPosition) & 1) != 0 ? Slot.ChainLength
            : Slot.InternalLength;

        public readonly Slot SlotAt(NodeKind kind, int offset) => PbtTrieNodeGroup.SlotAt(_destination, kind, offset);

        /// <summary>
        /// Appends the trailer — the bitmaps, and the <paramref name="stats"/> of the subtree the group
        /// roots — and returns the encoded length; 0 when nothing was appended, which the caller stores
        /// as a removal.
        /// </summary>
        /// <param name="stats">
        /// The whole subtree's, which the walk that appended the nodes cannot know: the stems below a
        /// boundary slot the walk left alone are never read. The producer hoists it instead.
        /// </param>
        public readonly int Finish(in PbtSubtreeStats stats)
        {
            if (_presence == 0) return 0;

            Span<byte> trailer = _destination.Slice(_offset, TrailerLength);
            BinaryPrimitives.WriteUInt32LittleEndian(trailer[PresenceTrailerOffset..], _presence);
            BinaryPrimitives.WriteUInt32LittleEndian(trailer[StemsTrailerOffset..], _stems);
            BinaryPrimitives.WriteUInt16LittleEndian(trailer[ChainsTrailerOffset..], (ushort)_chains);
            stats.Write(trailer[StatsTrailerOffset..]);
            trailer[FormatTrailerOffset] = (byte)_format;
            return _offset + TrailerLength;
        }

        private void MarkPresent(int position)
        {
            Debug.Assert((uint)position < PositionCount);
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
}
