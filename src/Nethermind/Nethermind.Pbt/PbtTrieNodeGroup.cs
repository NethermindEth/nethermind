// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Which levels of a <see cref="PbtTrieNodeGroup"/> an encoding stores internal nodes for; the last byte of every non-empty encoding.</summary>
/// <remarks>
/// Both encodings describe the same trie and fold to the same root — they differ only in how much of
/// the fold they write down — so a store may hold either at any key, and a group converts only when a
/// change rewrites it. The values are disjoint from <see cref="PbtNodeChain"/>'s, so an encoding of
/// either kind says which it is.
/// </remarks>
public enum PbtGroupFormat : byte
{
    /// <summary>Every level of the tile, the original encoding.</summary>
    EveryLevel = 0x01,

    /// <summary>
    /// Only the even group-relative levels (0, 2 and the boundary), skipping the internal nodes of
    /// levels 1 and 3 — a kept node's stored children are its grandchildren. A skipped node's hash is
    /// folded from its children wherever it is needed, so nothing about the trie is lost. Stem nodes
    /// are stored wherever they land, skipped level or not.
    /// </summary>
    Interleaved = 0x03,
}

/// <summary>
/// One 4-level tile of the stem trie: 31 post-order positions (16 boundary slots and 15 inner
/// positions), each absent, an internal node holding its cached hash (for a boundary slot, the
/// child group's root hash), a stem node holding its stem and 256-leaf subtree root, or — a boundary
/// slot only — the whole <see cref="PbtNodeChain"/> hanging from it.
/// </summary>
/// <remarks>
/// Positions follow the <see cref="StemLeafBlob"/> post-order numbering: boundary slot <c>i</c> sits
/// at <c>2i - popcount(i)</c>, the group root at 30, and a subtree covering <c>w</c> boundary slots
/// at position <c>p</c> has its children at <c>p - w</c> and <c>p - 1</c>.
/// <para>
/// The encoding is one entry per present position in ascending position order — a 32-byte cached
/// hash for an internal node, the 31-byte stem followed by its 32-byte leaf-subtree root for a stem
/// node (the stem node hash is derived, never stored), or a run's whole encoding — then a trailer of
/// the <see cref="NodeGroupBitmasks"/> that pin them, the <see cref="Stats"/> of the subtree this
/// group roots, and the format byte.
/// </para>
/// </remarks>
public readonly ref partial struct PbtTrieNodeGroup
{
    public enum NodeKind : byte
    {
        Absent = 0,
        Internal = 1,
        Stem = 2,
        Chain = 3,
    }

    /// <summary>A zero-copy, read-only view of one node — absent, internal, stem, or run.</summary>
    /// <remarks>
    /// Wraps the node's entry exactly as the group stores it, so its length alone gives its kind.
    /// The view borrows the group's buffer: a node that must outlive it is copied out with
    /// <see cref="ToValue"/>, bar a run, whose bytes are copied instead.
    /// </remarks>
    public readonly ref struct Slot
    {
        internal const int InternalLength = HashLength;
        internal const int StemLength = Stem.Length + HashLength;
        internal const int ChainLength = PbtNodeChain.EncodedLength;

        private readonly ReadOnlySpan<byte> _data;

        internal Slot(ReadOnlySpan<byte> data)
        {
            Debug.Assert(data.Length is 0 or InternalLength or StemLength or ChainLength);
            _data = data;
        }

        public NodeKind Kind => _data.Length switch
        {
            0 => NodeKind.Absent,
            InternalLength => NodeKind.Internal,
            StemLength => NodeKind.Stem,
            _ => NodeKind.Chain,
        };

        /// <summary>This node's 31-byte stem, sliced from the entry; valid only when <see cref="Kind"/> is <see cref="NodeKind.Stem"/>.</summary>
        public Stem Stem
        {
            get
            {
                Debug.Assert(Kind == NodeKind.Stem, "only a stem node carries a stem");
                return new Stem(_data[..Stem.Length]);
            }
        }

        /// <summary>This node's run, exactly as <see cref="PbtNodeChain"/> encodes one; valid only when <see cref="Kind"/> is <see cref="NodeKind.Chain"/>.</summary>
        public ReadOnlySpan<byte> ChainData
        {
            get
            {
                Debug.Assert(Kind == NodeKind.Chain, "only a run carries a chain encoding");
                return _data;
            }
        }

        /// <summary>
        /// The cached node hash of an internal node or of a run, or the 256-leaf subtree root of a
        /// stem node.
        /// </summary>
        public ValueHash256 Hash => Kind == NodeKind.Chain ? PbtNodeChain.NodeHashOf(_data) : new(_data[^HashLength..]);

        /// <summary>
        /// The hash this node contributes to its parent: zero when absent, the cached hash when
        /// internal or a run, or the derived stem node hash.
        /// </summary>
        public ValueHash256 NodeHash() => Kind switch
        {
            NodeKind.Absent => default,
            NodeKind.Stem => StemLeafBlob.ComputeStemNodeHash(Stem, Hash),
            _ => Hash,
        };

        /// <summary>Copies this node out of the buffer it borrows.</summary>
        /// <remarks>A run has no value form: it copies out as what it contributes to its parent, a boundary internal caching its node hash.</remarks>
        public ValueSlot ToValue() => Kind switch
        {
            NodeKind.Absent => default,
            NodeKind.Stem => StemSlot(Stem, Hash),
            _ => InternalSlot(Hash),
        };
    }

    /// <summary>An owned copy of a <see cref="Slot"/>, for a node that outlives the buffer it came from.</summary>
    /// <remarks>
    /// The payloads are fields, not properties: <see cref="ValueHash256.Bytes"/> over a
    /// property-returned copy is a span into a dead stack temporary the JIT may reuse.
    /// </remarks>
    public struct ValueSlot
    {
        public NodeKind Kind;
        public Stem Stem;

        /// <inheritdoc cref="Slot.Hash"/>
        public ValueHash256 Hash;

        /// <inheritdoc cref="Slot.NodeHash"/>
        public readonly ValueHash256 NodeHash() => Kind switch
        {
            NodeKind.Absent => default,
            NodeKind.Internal => Hash,
            _ => StemLeafBlob.ComputeStemNodeHash(Stem, Hash),
        };
    }

    private const int EntriesOffset = 0;

    private const int PresenceTrailerOffset = 0;
    private const int StemsTrailerOffset = PresenceTrailerOffset + sizeof(uint);
    private const int ChainsTrailerOffset = StemsTrailerOffset + sizeof(uint);
    private const int StatsTrailerOffset = ChainsTrailerOffset + sizeof(ushort);
    private const int FormatTrailerOffset = StatsTrailerOffset + PbtSubtreeStats.EncodedLength;
    private const int TrailerLength = FormatTrailerOffset + sizeof(byte);

    private const int OverheadLength = EntriesOffset + TrailerLength;
    private const int HashLength = 32;

    /// <summary>What a run's entry takes beyond the cached node hash every present position contributes.</summary>
    private const int ChainExtraLength = Slot.ChainLength - HashLength;

    /// <summary>
    /// An upper bound on an encoding's length: all 31 positions present, and the 16 disjoint boundary
    /// ranges a stem or a run can terminate all terminated by the longer of the two. An over-estimate
    /// now that the root position holds no stored internal node; buffers are sized by the exact
    /// <see cref="EncodedLength"/>.
    /// </summary>
    public const int MaxEncodedLength = OverheadLength + PbtLayout.TrieNodeGroupPositionCount * HashLength + PbtLayout.TrieNodeGroupBoundarySlots * ChainExtraLength;

    /// <summary>
    /// The encoded length of the group <paramref name="masks"/> describes, which they pin exactly:
    /// every node contributes a hash, and a stem node its stem or a run the rest of its encoding as
    /// well.
    /// </summary>
    public static int EncodedLength(NodeGroupBitmasks masks) =>
        OverheadLength
        + BitOperations.PopCount(masks.Presence) * HashLength
        + BitOperations.PopCount(masks.Stems) * Stem.Length
        + BitOperations.PopCount(masks.Chains) * ChainExtraLength;

    private readonly ReadOnlySpan<byte> _data;
    private readonly NodeGroupBitmasks _masks;

    private PbtTrieNodeGroup(ReadOnlySpan<byte> data, PbtGroupFormat format, NodeGroupBitmasks masks)
    {
        _data = data;
        Format = format;
        _masks = masks;
    }

    /// <summary>True for the default group, the absence sentinel; a stored group is never empty.</summary>
    public bool IsEmpty => _data.IsEmpty;

    /// <summary>Which encoding this group is in; meaningless for the empty group, which is neither.</summary>
    public PbtGroupFormat Format { get; }

    /// <summary>
    /// What the subtree rooted at this group amounts to; the empty group's, an absent subtree's, is
    /// <c>default</c>.
    /// </summary>
    public PbtSubtreeStats Stats => IsEmpty ? default : PbtSubtreeStats.Read(_data[^TrailerLength..][StatsTrailerOffset..]);

    /// <summary>Reads the node at <paramref name="position"/> on demand, borrowing from the wrapped span.</summary>
    /// <param name="position">
    /// The node's index in the tile's post-order (depth-first) numbering, in <c>[0, <see cref="PbtLayout.TrieNodeGroupPositionCount"/>)</c>
    /// — an internal DFS position, not the boundary-slot (leaf) index. The 16 boundary slots sit at
    /// <see cref="PbtLayout.TrieNodeGroupBoundarySlotPosition(int)"/> and the group root at <see cref="PbtLayout.TrieNodeGroupRootPosition"/>; see the type
    /// remarks for the full numbering.
    /// </param>
    public Slot this[int position]
    {
        get
        {
            NodeKind kind = KindAt(position);
            return kind == NodeKind.Absent ? default : SlotAt(_data, kind, EntryOffset(position));
        }
    }

    /// <remarks>
    /// This is the kind the <em>encoding</em> holds, which for a <see cref="PbtGroupFormat.Interleaved"/>
    /// group is not quite the kind the trie holds: an internal node at a skipped level has no entry
    /// and reads as <see cref="NodeKind.Absent"/>, though the trie has a node there. Only its hash is
    /// recoverable, by folding its two children — which is what the rebuild does, and why nothing
    /// else asks about an inner position.
    /// </remarks>
    /// <param name="position"><inheritdoc cref="this[int]" path="/param[@name='position']"/></param>
    public NodeKind KindAt(int position)
    {
        uint bit = 1u << position;
        return (_masks.Presence & bit) == 0 ? NodeKind.Absent
            : (_masks.Stems & bit) != 0 ? NodeKind.Stem
            : PbtLayout.TrieNodeGroupIsBoundaryPosition(position) && (_masks.Chains >> PbtLayout.TrieNodeGroupBoundarySlot(position) & 1) != 0 ? NodeKind.Chain
            : NodeKind.Internal;
    }

    /// <summary>
    /// The offset of the entry of the node at <paramref name="position"/>, for a consumer that holds
    /// on to a node rather than reading it out; meaningful only where a node is present.
    /// </summary>
    /// <remarks>
    /// <see cref="PbtLayout.TrieNodeGroupBoundarySlot"/> counts the boundary positions below <paramref name="position"/>, so
    /// it doubles as the slot bound on the runs below it — whether or not the position is a boundary
    /// one itself.
    /// </remarks>
    internal int EntryOffset(int position)
    {
        uint below = (1u << position) - 1;
        return EntriesOffset
            + BitOperations.PopCount(_masks.Presence & below) * HashLength
            + BitOperations.PopCount(_masks.Stems & below) * Stem.Length
            + BitOperations.PopCount(_masks.Chains & ((1u << PbtLayout.TrieNodeGroupBoundarySlot(position)) - 1)) * ChainExtraLength;
    }

    /// <summary>
    /// The bitmaps of the subtree rooted at <paramref name="position"/> and covering
    /// <paramref name="width"/> boundary slots — the shape this group gives it, by position.
    /// </summary>
    internal NodeGroupBitmasks SubtreeBitmaps(int position, int width)
    {
        uint mask = PbtLayout.TrieNodeGroupSubtreeBitmask(position, width);
        return new NodeGroupBitmasks(_masks.Presence & mask, _masks.Stems & mask, _masks.Chains & PbtLayout.TrieNodeGroupBoundaryBitmask(mask));
    }

    /// <summary>
    /// That subtree's entries, exactly as this group encodes them, for a producer copying a whole
    /// subtree rather than rebuilding it node by node.
    /// </summary>
    /// <remarks>
    /// A subtree's positions are contiguous and entries are stored in ascending position order, so its
    /// entries are one unbroken run — ending at its root, the highest position it holds. The run starts
    /// where the entries below it end, which <see cref="EntryOffset"/> gives whether or not
    /// <see cref="PbtLayout.TrieNodeGroupFirstSubtreePosition"/> itself holds a node, and <paramref name="masks"/> (from
    /// <see cref="SubtreeBitmaps"/>) pins its length — so neither needs a walk to find.
    /// <see cref="EncodedLength"/> counts a whole encoding's overhead, which a run of entries carries
    /// none of.
    /// </remarks>
    internal ReadOnlySpan<byte> SubtreeEntries(int position, int width, NodeGroupBitmasks masks) =>
        _data.Slice(EntryOffset(PbtLayout.TrieNodeGroupFirstSubtreePosition(position, width)), EncodedLength(masks) - OverheadLength);

    /// <summary>
    /// Reads the node of kind <paramref name="kind"/> whose entry starts at <paramref name="offset"/>
    /// out of the encoding <paramref name="data"/>, for a producer holding an entry offset rather than
    /// a position.
    /// </summary>
    internal static Slot SlotAt(ReadOnlySpan<byte> data, NodeKind kind, int offset) => kind switch
    {
        NodeKind.Absent => default,
        NodeKind.Stem => new Slot(data.Slice(offset, Slot.StemLength)),
        NodeKind.Chain => new Slot(data.Slice(offset, Slot.ChainLength)),
        _ => new Slot(data.Slice(offset, Slot.InternalLength)),
    };

    public static ValueSlot InternalSlot(in ValueHash256 hash) => new() { Kind = NodeKind.Internal, Hash = hash };

    public static ValueSlot StemSlot(in Stem stem, in ValueHash256 leafSubtreeRoot) =>
        new() { Kind = NodeKind.Stem, Stem = stem, Hash = leafSubtreeRoot };

    /// <summary>
    /// The group's shape at its boundary, gathered down into slot order: bit <c>i</c> of
    /// <see cref="NodeGroupBitmasks.Presence"/> is set where boundary slot <c>i</c> holds a node, of
    /// <see cref="NodeGroupBitmasks.Stems"/> where that node is a stem and of
    /// <see cref="NodeGroupBitmasks.Chains"/> where it is a run. All are zero for the empty group.
    /// </summary>
    /// <remarks>
    /// A boundary slot is never a skipped level, so this reads the same under either
    /// <see cref="PbtGroupFormat"/>; <see cref="Decode"/> keeps the stems and runs bitmaps disjoint
    /// subsets of the presence one, so neither needs masking against it.
    /// </remarks>
    internal NodeGroupBitmasks BoundaryShape() => new(PbtLayout.TrieNodeGroupBoundaryBitmask(_masks.Presence), PbtLayout.TrieNodeGroupBoundaryBitmask(_masks.Stems), _masks.Chains);

    /// <summary>
    /// Validates a non-empty group encoding (bitmaps and implied length) and wraps it as a
    /// read-only view; the returned group borrows <paramref name="data"/> and reads slots on demand.
    /// </summary>
    public static PbtTrieNodeGroup Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < OverheadLength) throw new InvalidDataException($"Trie node group too short: {data.Length} bytes");

        ReadOnlySpan<byte> trailer = data[^TrailerLength..];
        PbtGroupFormat format = (PbtGroupFormat)trailer[FormatTrailerOffset];
        if (format is not (PbtGroupFormat.EveryLevel or PbtGroupFormat.Interleaved))
        {
            throw new InvalidDataException($"Trie node group: unexpected format byte 0x{(byte)format:x2}");
        }

        NodeGroupBitmasks masks = new(
            BinaryPrimitives.ReadUInt32LittleEndian(trailer[PresenceTrailerOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(trailer[StemsTrailerOffset..]),
            BinaryPrimitives.ReadUInt16LittleEndian(trailer[ChainsTrailerOffset..]));
        (uint presence, uint stems, uint chains) = masks;
        if (presence >> PbtLayout.TrieNodeGroupPositionCount != 0 || (stems & ~presence) != 0)
        {
            throw new InvalidDataException("Invalid trie node group bitmaps");
        }

        // a run hangs from a boundary slot, and holds it: the slot roots neither a stem nor a child group
        if ((chains & ~(PbtLayout.TrieNodeGroupBoundaryBitmask(presence) & ~PbtLayout.TrieNodeGroupBoundaryBitmask(stems))) != 0)
        {
            throw new InvalidDataException("Invalid trie node group run bitmap");
        }

        // The root's internal node is folded from its children and already cached in the parent group's
        // boundary slot, so it is never stored — whatever the format. Only a stem, which nothing
        // recomputes, may occupy the root position.
        if ((presence & (1u << PbtLayout.TrieNodeGroupRootPosition) & ~stems) != 0)
        {
            throw new InvalidDataException("Trie node group stores an internal node at the root position");
        }

        // An interleaved group folds its odd levels' internal nodes rather than storing them, so a
        // present skipped position is a stem.
        if (PbtLayout.TrieNodeGroupHoldsSkippedInternal(format, presence, stems))
        {
            throw new InvalidDataException("Interleaved trie node group holds an internal node at a skipped level");
        }

        int expectedLength = EncodedLength(masks);
        if (data.Length != expectedLength)
        {
            throw new InvalidDataException($"Trie node group length {data.Length} does not match its bitmaps (expected {expectedLength})");
        }

        return new PbtTrieNodeGroup(data, format, masks);
    }
}
