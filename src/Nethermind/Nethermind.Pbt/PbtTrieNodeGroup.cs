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
/// change rewrites it. The values are disjoint from <see cref="PbtNodeChain"/>'s, which is what
/// discriminates a group from a run in the store.
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
/// child group's root hash), or a stem node holding its stem and 256-leaf subtree root.
/// </summary>
/// <remarks>
/// Positions follow the <see cref="StemLeafBlob"/> post-order numbering: boundary slot <c>i</c>
/// sits at <c>2i - popcount(i)</c>, the group root at 30, and a subtree covering <c>w</c> boundary
/// slots at position <c>p</c> has its children at <c>p - w</c> and <c>p - 1</c>. The encoding is one
/// entry per present position in ascending position order — a 32-byte
/// cached hash for an internal node, or the 31-byte stem followed by its 32-byte leaf-subtree root
/// for a stem node (the stem node hash is derived, never stored) — and last a trailer of two
/// little-endian 32-bit bitmaps over the positions, presence and stem, then the group's
/// <see cref="PbtSubtreeStats"/> and the format byte. The internal node at the group root (position 30) is not stored
/// either: it is folded from its children and already cached in the parent group's boundary slot,
/// so only a stem may occupy the root position.
/// A stem position has no present positions below it in the group and no child groups below it.
/// An empty group encodes to zero bytes, the store's removal marker.
/// <para>
/// The trailer counts stems twice over, and means a different thing by each: the stem bitmap says
/// which of <em>this tile's</em> positions hold a stem node, while <see cref="Stats"/> counts the
/// stems of the <em>whole subtree</em>, the ones below this group's child groups included.
/// </para>
/// <para>
/// A <see cref="PbtGroupFormat.Interleaved"/> group stores no internal node at an odd group-relative level,
/// folding those hashes on demand instead; see <see cref="PbtGroupFormat"/>. Only the fold reads them, so a
/// node there reads as <see cref="NodeKind.Absent"/> from the outside though the trie holds one —
/// see <see cref="KindAt"/>. A stem is stored wherever its shortest unique prefix falls, skipped
/// level or not, so the bitmaps still pin the entries exactly.
/// </para>
/// </remarks>
public readonly ref struct PbtTrieNodeGroup
{
    public enum NodeKind : byte
    {
        Absent = 0,
        Internal = 1,
        Stem = 2,
    }

    /// <summary>A zero-copy, read-only view of one node — absent, internal, or stem.</summary>
    /// <remarks>
    /// Wraps the node's entry exactly as the group stores it, so its length alone gives its kind.
    /// The view borrows the group's buffer: a node that must outlive it is copied out with
    /// <see cref="ToValue"/>.
    /// </remarks>
    public readonly ref struct Slot
    {
        internal const int InternalLength = HashLength;
        internal const int StemLength = Stem.Length + HashLength;

        private readonly ReadOnlySpan<byte> _data;

        internal Slot(ReadOnlySpan<byte> data)
        {
            Debug.Assert(data.Length is 0 or InternalLength or StemLength);
            _data = data;
        }

        public NodeKind Kind => _data.Length switch
        {
            0 => NodeKind.Absent,
            InternalLength => NodeKind.Internal,
            _ => NodeKind.Stem,
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

        /// <summary>The cached node hash of an internal node, or the 256-leaf subtree root of a stem node.</summary>
        public ValueHash256 Hash => new(_data[^HashLength..]);

        /// <summary>
        /// The hash this node contributes to its parent: zero when absent, the cached hash when
        /// internal, or the derived stem node hash.
        /// </summary>
        public ValueHash256 NodeHash() => Kind switch
        {
            NodeKind.Absent => default,
            NodeKind.Internal => Hash,
            _ => StemLeafBlob.ComputeStemNodeHash(Stem, Hash),
        };

        /// <summary>Copies this node out of the buffer it borrows.</summary>
        public ValueSlot ToValue() => Kind switch
        {
            NodeKind.Absent => default,
            NodeKind.Internal => InternalSlot(Hash),
            _ => StemSlot(Stem, Hash),
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

    /// <summary>Trie levels covered by one group: a group rooted at depth d has its boundary slots at depth d + 4.</summary>
    public const int LevelsPerGroup = 4;
    public const int BoundarySlots = 1 << LevelsPerGroup;
    public const int PositionCount = 2 * BoundarySlots - 1;
    public const int RootPosition = 2 * BoundarySlots - 2;

    /// <summary>The deepest group root depth; that group's boundary is the 248-bit stem level, where every node is a stem.</summary>
    public const int MaxGroupDepth = Stem.LengthInBits - LevelsPerGroup;

    private const int EntriesOffset = 0;

    private const int PresenceTrailerOffset = 0;
    private const int StemsTrailerOffset = PresenceTrailerOffset + sizeof(uint);
    private const int StatsTrailerOffset = StemsTrailerOffset + sizeof(uint);
    private const int FormatTrailerOffset = StatsTrailerOffset + PbtSubtreeStats.EncodedLength;
    private const int TrailerLength = FormatTrailerOffset + sizeof(byte);

    private const int OverheadLength = EntriesOffset + TrailerLength;
    private const int HashLength = 32;

    /// <summary>
    /// An upper bound on an encoding's length: all 31 positions present, 16 of them stems (each stem
    /// terminates a disjoint boundary range). An over-estimate now that the root position holds no
    /// stored internal node; buffers are sized by the exact <see cref="EncodedLength"/>.
    /// </summary>
    public const int MaxEncodedLength = OverheadLength + PositionCount * HashLength + BoundarySlots * Stem.Length;

    /// <summary>
    /// The encoded length of the group described by the bitmaps <paramref name="presence"/> and
    /// <paramref name="stems"/>, which pin it exactly: every node contributes a hash and a stem node
    /// its stem as well.
    /// </summary>
    public static int EncodedLength(uint presence, uint stems) =>
        OverheadLength + BitOperations.PopCount(presence) * HashLength + BitOperations.PopCount(stems) * Stem.Length;

    /// <summary>Bit set at <see cref="BoundaryPosition"/>(i) for each boundary slot i.</summary>
    private const uint BoundaryPositionsMask = 0x06CD8D9Bu;

    /// <summary>
    /// Bit set at each position <see cref="PbtGroupFormat.Interleaved"/> stores no internal node at: the odd
    /// group-relative levels, 14 and 29 (level 1) and 2, 5, 9, 12, 17, 20, 24 and 27 (level 3).
    /// </summary>
    /// <remarks>
    /// A position's level follows from the boundary slots its subtree covers — 16 at the root, then 8,
    /// 4, 2 and 1 at the boundary — so the levels alternate with the width halving, and
    /// <see cref="StoresInternalAtWidth"/> says the same thing by width where a walk has one to hand.
    /// Disjoint from <see cref="BoundaryPositionsMask"/>: a boundary slot is a level of its own.
    /// </remarks>
    private const uint SkippedPositionsMask = 0x29125224u;

    /// <summary>The widths whose level <see cref="PbtGroupFormat.Interleaved"/> stores an internal node at: 16, 4 and 1.</summary>
    private const int KeptWidths = 0b10101;

    /// <summary>Whether <paramref name="format"/> stores no internal node at <paramref name="position"/>.</summary>
    /// <remarks>A stem node is stored at every position; only an internal node's hash is recomputable.</remarks>
    public static bool IsSkippedPosition(PbtGroupFormat format, int position) =>
        format == PbtGroupFormat.Interleaved && (SkippedPositionsMask & (1u << position)) != 0;

    /// <summary>
    /// Whether <paramref name="format"/> stores an internal node at the position whose subtree covers
    /// <paramref name="width"/> boundary slots.
    /// </summary>
    public static bool StoresInternalAtWidth(PbtGroupFormat format, int width) =>
        format == PbtGroupFormat.EveryLevel || (width & KeptWidths) != 0;

    private readonly ReadOnlySpan<byte> _data;
    private readonly uint _presence;
    private readonly uint _stems;

    private PbtTrieNodeGroup(ReadOnlySpan<byte> data, PbtGroupFormat format, uint presence, uint stems)
    {
        _data = data;
        Format = format;
        _presence = presence;
        _stems = stems;
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
    /// The node's index in the tile's post-order (depth-first) numbering, in <c>[0, <see cref="PositionCount"/>)</c>
    /// — an internal DFS position, not the boundary-slot (leaf) index. The 16 boundary slots sit at
    /// <see cref="BoundaryPosition(int)"/> and the group root at <see cref="RootPosition"/>; see the type
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
        return (_presence & bit) == 0 ? NodeKind.Absent
            : (_stems & bit) != 0 ? NodeKind.Stem
            : NodeKind.Internal;
    }

    /// <summary>
    /// The offset of the entry of the node at <paramref name="position"/>, for a consumer that holds
    /// on to a node rather than reading it out; meaningful only where a node is present.
    /// </summary>
    internal int EntryOffset(int position)
    {
        uint below = (1u << position) - 1;
        return EntriesOffset
            + BitOperations.PopCount(_presence & below) * HashLength
            + BitOperations.PopCount(_stems & below) * Stem.Length;
    }

    /// <summary>The lowest position of the subtree rooted at <paramref name="position"/> covering <paramref name="width"/> boundary slots.</summary>
    private static int FirstSubtreePosition(int position, int width) => position - 2 * width + 2;

    private static uint SubtreeMask(int position, int width) =>
        ((1u << (position + 1)) - 1) & ~((1u << FirstSubtreePosition(position, width)) - 1);

    /// <summary>
    /// The presence and stem bitmaps of the subtree rooted at <paramref name="position"/> and covering
    /// <paramref name="width"/> boundary slots — the shape this group gives it.
    /// </summary>
    internal void SubtreeBitmaps(int position, int width, out uint presence, out uint stems)
    {
        uint mask = SubtreeMask(position, width);
        presence = _presence & mask;
        stems = _stems & mask;
    }

    /// <summary>
    /// That subtree's entries, exactly as this group encodes them, for a producer copying a whole
    /// subtree rather than rebuilding it node by node.
    /// </summary>
    /// <remarks>
    /// A subtree's positions are contiguous and entries are stored in ascending position order, so its
    /// entries are one unbroken run — ending at its root, the highest position it holds. The run starts
    /// where the entries below it end, which <see cref="EntryOffset"/> gives whether or not
    /// <see cref="FirstSubtreePosition"/> itself holds a node, and <paramref name="presence"/> and
    /// <paramref name="stems"/> (from <see cref="SubtreeBitmaps"/>) pin its length — so neither needs a
    /// walk to find. <see cref="EncodedLength"/> counts a whole encoding's overhead, which a run of
    /// entries carries none of.
    /// </remarks>
    internal ReadOnlySpan<byte> SubtreeEntries(int position, int width, uint presence, uint stems) =>
        _data.Slice(EntryOffset(FirstSubtreePosition(position, width)), EncodedLength(presence, stems) - OverheadLength);

    /// <summary>
    /// Reads the node of kind <paramref name="kind"/> whose entry starts at <paramref name="offset"/>
    /// out of the encoding <paramref name="data"/>, for a producer holding an entry offset rather than
    /// a position.
    /// </summary>
    internal static Slot SlotAt(ReadOnlySpan<byte> data, NodeKind kind, int offset) => kind == NodeKind.Absent
        ? default
        : new Slot(data.Slice(offset, kind == NodeKind.Stem ? Slot.StemLength : Slot.InternalLength));

    public static ValueSlot InternalSlot(in ValueHash256 hash) => new() { Kind = NodeKind.Internal, Hash = hash };

    public static ValueSlot StemSlot(in Stem stem, in ValueHash256 leafSubtreeRoot) =>
        new() { Kind = NodeKind.Stem, Stem = stem, Hash = leafSubtreeRoot };

    /// <summary>The post-order position of boundary slot <paramref name="slot"/>.</summary>
    public static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    public static bool IsBoundaryPosition(int position) => (BoundaryPositionsMask & (1u << position)) != 0;

    public static int BoundarySlot(int position) => BitOperations.PopCount(BoundaryPositionsMask & ((1u << position) - 1));

    /// <summary>
    /// The group's boundary shape as slot-indexed bitmaps: bit <c>i</c> of <paramref name="occupied"/> is
    /// set where boundary slot <c>i</c> holds a node, and of <paramref name="stems"/> where that node is a
    /// stem. Both are zero for the empty group.
    /// </summary>
    /// <remarks>
    /// A boundary slot is never a skipped level, so these read the same under either
    /// <see cref="PbtGroupFormat"/>; <see cref="Decode"/> keeps the stems bitmap a subset of the presence
    /// one, so <paramref name="stems"/> needs no masking against <paramref name="occupied"/>.
    /// </remarks>
    internal void BoundaryShape(out uint occupied, out uint stems)
    {
        occupied = BoundaryBits(_presence);
        stems = BoundaryBits(_stems);
    }

    /// <summary>Gathers the sixteen <see cref="BoundaryPositionsMask"/> bits of <paramref name="positions"/> down into slot order.</summary>
    /// <remarks>
    /// A software PEXT of the constant mask, whose bits fall in four groups of four — positions
    /// <c>o + {0, 1, 3, 4}</c> for <c>o</c> in 0, 7, 15 and 22 — so each group compacts with one shift
    /// and the four pack together. Branch-free and ISA-independent, unlike <c>Bmi2.ParallelBitExtract</c>.
    /// </remarks>
    internal static uint BoundaryBits(uint positions) =>
        CompactGroup(positions)
        | (CompactGroup(positions >> 7) << 4)
        | (CompactGroup(positions >> 15) << 8)
        | (CompactGroup(positions >> 22) << 12);

    /// <summary>Compacts one group's bits — at 0, 1, 3 and 4 — down into the low four.</summary>
    private static uint CompactGroup(uint group) => (group & 0b0011u) | ((group >> 1) & 0b1100u);

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

        uint presence = BinaryPrimitives.ReadUInt32LittleEndian(trailer[PresenceTrailerOffset..]);
        uint stems = BinaryPrimitives.ReadUInt32LittleEndian(trailer[StemsTrailerOffset..]);
        if (presence >> PositionCount != 0 || (stems & ~presence) != 0)
        {
            throw new InvalidDataException("Invalid trie node group bitmaps");
        }

        // The root's internal node is folded from its children and already cached in the parent group's
        // boundary slot, so it is never stored — whatever the format. Only a stem, which nothing
        // recomputes, may occupy the root position.
        if ((presence & (1u << RootPosition) & ~stems) != 0)
        {
            throw new InvalidDataException("Trie node group stores an internal node at the root position");
        }

        // An interleaved group folds its odd levels' internal nodes rather than storing them, so a
        // present skipped position is a stem.
        if (format == PbtGroupFormat.Interleaved && (presence & SkippedPositionsMask & ~stems) != 0)
        {
            throw new InvalidDataException("Interleaved trie node group holds an internal node at a skipped level");
        }

        int expectedLength = EncodedLength(presence, stems);
        if (data.Length != expectedLength)
        {
            throw new InvalidDataException($"Trie node group length {data.Length} does not match its bitmaps (expected {expectedLength})");
        }

        return new PbtTrieNodeGroup(data, format, presence, stems);
    }

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

        /// <summary>
        /// Appends a whole subtree at once — <paramref name="entries"/>, holding the positions
        /// <paramref name="presence"/> of which <paramref name="stems"/> are stem nodes — returning its
        /// root's entry offset.
        /// </summary>
        /// <remarks>
        /// The run is another group's encoding of that same subtree (<see cref="SubtreeEntries"/>), which
        /// is byte-for-byte what appending its nodes one at a time would produce, so a producer that finds
        /// a subtree unchanged copies it rather than folding it. Its root is its highest position, hence
        /// the last entry of the run.
        /// </remarks>
        public int AppendSubtree(uint presence, uint stems, ReadOnlySpan<byte> entries)
        {
            Debug.Assert(presence != 0, "an absent subtree has no entries to append");
            Debug.Assert((stems & ~presence) == 0, "only a present position holds a stem");
            Debug.Assert(_presence >> BitOperations.TrailingZeroCount(presence) == 0, "nodes must be appended in ascending position order");
            Debug.Assert(
                _format == PbtGroupFormat.EveryLevel || (presence & SkippedPositionsMask & ~stems) == 0,
                "a verbatim run must be in this group's own format");

            _presence |= presence;
            _stems |= stems;

            int rootPosition = BitOperations.Log2(presence);
            int rootLength = (stems >> rootPosition & 1) != 0 ? Slot.StemLength : Slot.InternalLength;
            int rootOffset = _offset + entries.Length - rootLength;
            Write(entries);
            return rootOffset;
        }

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
