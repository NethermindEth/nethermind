// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// One 4-level tile of the stem trie: 31 post-order positions (16 boundary slots and 15 inner
/// positions), each absent, an internal node holding its cached hash (for a boundary slot, the
/// child group's root hash), or a stem node holding its stem and 256-leaf subtree root.
/// </summary>
/// <remarks>
/// Positions follow the <see cref="StemLeafBlob"/> post-order numbering: boundary slot <c>i</c>
/// sits at <c>2i - popcount(i)</c>, the group root at 30, and a subtree covering <c>w</c> boundary
/// slots at position <c>p</c> has its children at <c>p - w</c> and <c>p - 1</c>. The encoding is
/// two little-endian 32-bit bitmaps over the positions — presence and stem — followed by one entry
/// per present position in ascending position order: a 32-byte cached hash for an internal node, or
/// the 31-byte stem followed by its 32-byte leaf-subtree root for a stem node (the stem node hash
/// is derived, never stored). Entry offsets and the total length follow from the bitmaps, keeping
/// the encoding deterministic. A stem position has no present positions below it in the group and
/// no child groups below it. An empty group encodes to zero bytes, the store's removal marker.
/// </remarks>
public readonly ref struct PbtTrieNodeGroup
{
    public enum NodeKind : byte
    {
        Absent = 0,
        Internal = 1,
        Stem = 2,
    }

    /// <summary>One position's node: absent, internal, or stem.</summary>
    /// <remarks>
    /// The payloads are fields, not properties: <see cref="ValueHash256.Bytes"/> over a
    /// property-returned copy is a span into a dead stack temporary the JIT may reuse.
    /// </remarks>
    public struct Slot
    {
        public NodeKind Kind;
        public Stem Stem;

        /// <summary>The cached node hash of an internal node, or the 256-leaf subtree root of a stem node.</summary>
        public ValueHash256 Hash;

        /// <summary>
        /// The hash this node contributes to its parent: zero when absent, the cached hash when
        /// internal, or the derived stem node hash.
        /// </summary>
        public readonly ValueHash256 NodeHash() => Kind switch
        {
            NodeKind.Absent => default,
            NodeKind.Internal => Hash,
            _ => StemLeafBlob.ComputeStemNodeHash(Stem, Hash),
        };
    }

    /// <summary>Trie levels covered by one group: a group rooted at depth d has its boundary slots at depth d + 4.</summary>
    public const int LevelsPerGroup = 4;
    public const int BoundarySlots = 1 << LevelsPerGroup;
    public const int PositionCount = 2 * BoundarySlots - 1;
    public const int RootPosition = 2 * BoundarySlots - 2;

    /// <summary>The deepest group root depth; that group's boundary is the 248-bit stem level, where every node is a stem.</summary>
    public const int MaxGroupDepth = Stem.LengthInBits - LevelsPerGroup;

    private const int HeaderLength = 2 * sizeof(uint);
    private const int HashLength = 32;

    /// <summary>Largest encoding: all 31 positions present, 16 of them stems (each stem terminates a disjoint boundary range).</summary>
    public const int MaxEncodedLength = HeaderLength + PositionCount * HashLength + BoundarySlots * Stem.Length;

    /// <summary>Bit set at <see cref="BoundaryPosition"/>(i) for each boundary slot i.</summary>
    private const uint BoundaryPositionsMask = 0x06CD8D9Bu;

    private readonly ReadOnlySpan<byte> _data;
    private readonly uint _presence;
    private readonly uint _stems;

    private PbtTrieNodeGroup(ReadOnlySpan<byte> data, uint presence, uint stems)
    {
        _data = data;
        _presence = presence;
        _stems = stems;
    }

    /// <summary>True for the default group, the absence sentinel; a stored group is never empty.</summary>
    public bool IsEmpty => _data.IsEmpty;

    /// <summary>Reads the node at <paramref name="position"/> on demand, borrowing from the wrapped span.</summary>
    /// <param name="position">
    /// The node's index in the tile's post-order (depth-first) numbering, in <c>[0, <see cref="PositionCount"/>)</c>
    /// — an internal DFS position, not the boundary-slot (leaf) index. The 16 boundary slots sit at
    /// <see cref="BoundaryPosition(int)"/> and the group root at <see cref="RootPosition"/>; see the type
    /// remarks for the full numbering.
    /// </param>
    public Slot this[int position]
    {
        get
        {
            uint bit = 1u << position;
            if ((_presence & bit) == 0) return default;

            uint below = bit - 1;
            int offset = HeaderLength
                + BitOperations.PopCount(_presence & below) * HashLength
                + BitOperations.PopCount(_stems & below) * Stem.Length;

            Slot slot = default;
            if ((_stems & bit) != 0)
            {
                slot.Kind = NodeKind.Stem;
                slot.Stem = new Stem(_data.Slice(offset, Stem.Length));
                offset += Stem.Length;
            }
            else
            {
                slot.Kind = NodeKind.Internal;
            }

            slot.Hash = new ValueHash256(_data.Slice(offset, HashLength));
            return slot;
        }
    }

    public static Slot InternalSlot(in ValueHash256 hash) => new() { Kind = NodeKind.Internal, Hash = hash };

    public static Slot StemSlot(in Stem stem, in ValueHash256 leafSubtreeRoot) =>
        new() { Kind = NodeKind.Stem, Stem = stem, Hash = leafSubtreeRoot };

    /// <summary>The post-order position of boundary slot <paramref name="slot"/>.</summary>
    public static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    public static bool IsBoundaryPosition(int position) => (BoundaryPositionsMask & (1u << position)) != 0;

    /// <summary>The boundary slot index of boundary <paramref name="position"/>.</summary>
    public static int BoundarySlot(int position) => BitOperations.PopCount(BoundaryPositionsMask & ((1u << position) - 1));

    /// <summary>
    /// Validates a non-empty group encoding (bitmaps and implied length) and wraps it as a
    /// read-only view; the returned group borrows <paramref name="data"/> and reads slots on demand.
    /// </summary>
    public static PbtTrieNodeGroup Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength) throw new InvalidDataException($"Trie node group too short: {data.Length} bytes");

        uint presence = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint stems = BinaryPrimitives.ReadUInt32LittleEndian(data[sizeof(uint)..]);
        if (presence >> PositionCount != 0 || (stems & ~presence) != 0)
        {
            throw new InvalidDataException("Invalid trie node group bitmaps");
        }

        int expectedLength = HeaderLength + BitOperations.PopCount(presence) * HashLength + BitOperations.PopCount(stems) * Stem.Length;
        if (data.Length != expectedLength)
        {
            throw new InvalidDataException($"Trie node group length {data.Length} does not match its bitmaps (expected {expectedLength})");
        }

        return new PbtTrieNodeGroup(data, presence, stems);
    }

    /// <summary>
    /// Encodes a group into a caller-supplied buffer one node at a time, as the producer walks the
    /// group, rather than from a materialized set of slots.
    /// </summary>
    /// <remarks>
    /// Nodes must be appended in ascending position order — the order a post-order walk of the group
    /// visits them, and the order the encoding stores them in — which makes each entry's offset
    /// implicit in the append cursor: no offset math is needed while building, and the bitmap header
    /// is backfilled by <see cref="Finish"/> once the walk is done. An appended entry keeps a stable
    /// offset, so a producer can read a node back through <see cref="NodeHashAt"/> instead of
    /// carrying hashes up its walk by value.
    /// </remarks>
    public ref struct Builder(Span<byte> destination)
    {
        private readonly Span<byte> _destination = destination;
        private uint _presence;
        private uint _stems;
        private int _offset = HeaderLength;

        /// <summary>Appends the internal node at <paramref name="position"/>, returning its entry offset.</summary>
        public int AppendInternal(int position, in ValueHash256 hash)
        {
            Debug.Assert(hash != default, "internal nodes never cache a zero hash");
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

        /// <summary>The hash the node appended at <paramref name="offset"/> contributes to its parent.</summary>
        /// <remarks>Mirrors <see cref="Slot.NodeHash"/>, reading the entry back out of the buffer.</remarks>
        public readonly ValueHash256 NodeHashAt(NodeKind kind, int offset) => kind switch
        {
            NodeKind.Absent => default,
            NodeKind.Internal => new ValueHash256(_destination.Slice(offset, HashLength)),
            _ => StemLeafBlob.ComputeStemNodeHash(
                new Stem(_destination.Slice(offset, Stem.Length)),
                new ValueHash256(_destination.Slice(offset + Stem.Length, HashLength))),
        };

        /// <summary>Reads the node appended at <paramref name="offset"/> back as a <see cref="Slot"/>.</summary>
        public readonly Slot SlotAt(NodeKind kind, int offset) => kind == NodeKind.Stem
            ? StemSlot(
                new Stem(_destination.Slice(offset, Stem.Length)),
                new ValueHash256(_destination.Slice(offset + Stem.Length, HashLength)))
            : InternalSlot(new ValueHash256(_destination.Slice(offset, HashLength)));

        /// <summary>
        /// Writes the bitmap header and returns the encoded length; 0 when nothing was appended,
        /// which the caller stores as a removal.
        /// </summary>
        public readonly int Finish()
        {
            if (_presence == 0) return 0;

            BinaryPrimitives.WriteUInt32LittleEndian(_destination, _presence);
            BinaryPrimitives.WriteUInt32LittleEndian(_destination[sizeof(uint)..], _stems);
            return _offset;
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
