// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// What a trie node group holds, whatever its tiling: the kinds a position can be, and the entries
/// each of them encodes to.
/// </summary>
/// <remarks>
/// A node's entry says nothing about the tile's width, so this much is shared between the tilings
/// while the encoding around it — <see cref="PbtTrieNodeGroup{TLayout}"/> — is not.
/// </remarks>
public static class PbtTrieNodeGroup
{
    internal const int EntriesOffset = 0;
    internal const int HashLength = 32;

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

    public static ValueSlot InternalSlot(in ValueHash256 hash) => new() { Kind = NodeKind.Internal, Hash = hash };

    public static ValueSlot StemSlot(in Stem stem, in ValueHash256 leafSubtreeRoot) =>
        new() { Kind = NodeKind.Stem, Stem = stem, Hash = leafSubtreeRoot };

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
}

/// <summary>
/// One tile of the stem trie in the <typeparamref name="TLayout"/> tiling: its post-order positions
/// (<see cref="IPbtTileLayout.BoundarySlots"/> boundary slots and as many inner positions less one),
/// each absent, an internal node holding its cached hash (for a boundary slot, the child group's root
/// hash), a stem node holding its stem and 256-leaf subtree root, or — a boundary slot only — the
/// whole <see cref="PbtNodeChain"/> hanging from it.
/// </summary>
/// <remarks>
/// Positions follow the <see cref="StemLeafBlob"/> post-order numbering: boundary slot <c>i</c> sits
/// at <c>2i - popcount(i)</c>, the group root at <see cref="IPbtTileLayout.RootPosition"/>, and a
/// subtree covering <c>w</c> boundary slots at position <c>p</c> has its children at <c>p - w</c> and
/// <c>p - 1</c>.
/// <para>
/// The encoding is one entry per present position in ascending position order — a 32-byte cached
/// hash for an internal node, the 31-byte stem followed by its 32-byte leaf-subtree root for a stem
/// node (the stem node hash is derived, never stored), or a run's whole encoding — then a trailer of
/// the <see cref="NodeGroupBitmasks"/> that pin them, the <see cref="Stats"/> of the subtree this
/// group roots, and the format byte.
/// </para>
/// </remarks>
public readonly ref struct PbtTrieNodeGroup<TLayout> where TLayout : IPbtTileLayout
{
    private const int HashLength = PbtTrieNodeGroup.HashLength;
    private const int EntriesOffset = PbtTrieNodeGroup.EntriesOffset;

    internal static int StatsTrailerOffset => TLayout.MaskTrailerLength;
    internal static int FormatTrailerOffset => StatsTrailerOffset + PbtSubtreeStats.EncodedLength;
    internal static int TrailerLength => FormatTrailerOffset + sizeof(byte);

    private const int FixedTrailerLength = PbtSubtreeStats.EncodedLength + sizeof(byte);
    private static int OverheadLength => EntriesOffset + TrailerLength;
    private static int MaxOverheadLength => EntriesOffset + TLayout.MaxMaskTrailerLength + FixedTrailerLength;

    /// <summary>What a run's entry takes beyond the cached node hash every present position contributes.</summary>
    private const int ChainExtraLength = PbtTrieNodeGroup.Slot.ChainLength - HashLength;

    /// <summary>
    /// An upper bound on an encoding's length: every position present, and the disjoint boundary
    /// ranges a stem or a run can terminate all terminated by the longer of the two. An over-estimate
    /// now that the root position holds no stored internal node; a producer that knows its boundary
    /// takes the tighter <see cref="EncodedLengthBound"/> instead.
    /// </summary>
    public static int MaxEncodedLength => MaxOverheadLength + TLayout.PositionCount * HashLength + TLayout.BoundarySlots * ChainExtraLength;

    /// <summary>
    /// The encoded length of the group <paramref name="masks"/> describes, which they pin exactly:
    /// every node contributes a hash, and a stem node its stem or a run the rest of its encoding as
    /// well.
    /// </summary>
    public static int EncodedLength(NodeGroupBitmasks masks)
    {
        if (TLayout.PositionMaskWordCount > 2 || TLayout.BoundaryMaskWordCount > 1)
            throw new NotSupportedException("Wide trie node group masks require span-based length calculation");

        return OverheadLength
            + PbtLayout.PopCount(masks.Presence) * HashLength
            + PbtLayout.PopCount(masks.Stems) * Stem.Length
            + BitOperations.PopCount(masks.Chains) * ChainExtraLength;
    }

    /// <summary>
    /// An upper bound on the encoding a fold over the boundary values <paramref name="boundary"/>
    /// describes will produce, for a producer reserving room before it folds.
    /// </summary>
    /// <remarks>
    /// Takes the boundary shape — slot-indexed bits, as <see cref="BoundaryShape"/> gives — where
    /// <see cref="EncodedLength"/> takes the position-indexed ones a finished encoding has. Exact on
    /// stems and runs, both of which land at a boundary slot, so the masks count their entries
    /// outright. Loose only on internal nodes, of which a tile over <c>k</c> occupied slots holds at
    /// most one <c>min(w, k)</c> term per level of halving width <c>w</c>. A range the fold copies
    /// verbatim rather than rebuilding covers the same slots, so it is bounded alike.
    /// </remarks>
    internal static int EncodedLengthBound(BoundarySlotMasks boundary)
    {
        int occupiedSlots = BitOperations.PopCount(boundary.Presence);
        int nodes = occupiedSlots + 1;
        for (int width = 2; width < TLayout.BoundarySlots; width *= 2) nodes += Math.Min(width, occupiedSlots);
        return OverheadLength
            + nodes * HashLength
            + BitOperations.PopCount(boundary.Stems) * Stem.Length
            + BitOperations.PopCount(boundary.Chains) * ChainExtraLength;
    }

    internal static int EncodedLengthBound(ReadOnlySpan<ulong> presence, ReadOnlySpan<ulong> stems, ReadOnlySpan<ulong> chains)
    {
        int occupiedSlots = PbtBitset.PopCount(presence);
        int nodes = occupiedSlots + 1;
        for (int width = 2; width < TLayout.BoundarySlots; width *= 2) nodes += Math.Min(width, occupiedSlots);
        return MaxOverheadLength
            + nodes * HashLength
            + PbtBitset.PopCount(stems) * Stem.Length
            + PbtBitset.PopCount(chains) * ChainExtraLength;
    }

    private readonly ReadOnlySpan<byte> _data;
    private readonly ReadOnlySpan<byte> _presence;
    private readonly ReadOnlySpan<byte> _stems;
    private readonly ReadOnlySpan<byte> _chains;
    private readonly CompactBitmap256 _compactChains;

    private PbtTrieNodeGroup(
        ReadOnlySpan<byte> data,
        PbtGroupFormat format,
        ReadOnlySpan<byte> presence,
        ReadOnlySpan<byte> stems,
        ReadOnlySpan<byte> chains,
        CompactBitmap256 compactChains)
    {
        _data = data;
        _presence = presence;
        _stems = stems;
        _chains = chains;
        _compactChains = compactChains;
        Format = format;
    }

    /// <summary>True for the default group, the absence sentinel; a stored group is never empty.</summary>
    public bool IsEmpty => _data.IsEmpty;

    /// <summary>Which encoding this group is in; meaningless for the empty group, which is neither.</summary>
    public PbtGroupFormat Format { get; }

    /// <summary>
    /// What the subtree rooted at this group amounts to; the empty group's, an absent subtree's, is
    /// <c>default</c>.
    /// </summary>
    public PbtSubtreeStats Stats => IsEmpty ? default : PbtSubtreeStats.Read(_data[^FixedTrailerLength..]);

    /// <summary>Reads the node at <paramref name="position"/> on demand, borrowing from the wrapped span.</summary>
    /// <param name="position">
    /// The node's index in the tile's post-order (depth-first) numbering, in
    /// <c>[0, <see cref="IPbtTileLayout.PositionCount"/>)</c> — an internal DFS position, not the
    /// boundary-slot (leaf) index. The boundary slots sit at
    /// <see cref="PbtLayout.TrieNodeGroupBoundarySlotPosition(int)"/> and the group root at
    /// <see cref="IPbtTileLayout.RootPosition"/>; see the type remarks for the full numbering.
    /// </param>
    public PbtTrieNodeGroup.Slot this[int position]
    {
        get
        {
            PbtTrieNodeGroup.NodeKind kind = KindAt(position);
            return kind == PbtTrieNodeGroup.NodeKind.Absent ? default : PbtTrieNodeGroup.SlotAt(_data, kind, EntryOffset(position));
        }
    }

    /// <remarks>
    /// This is the kind the <em>encoding</em> holds, which for a format that skips a level is not
    /// quite the kind the trie holds: an internal node at a skipped level has no entry and reads as
    /// <see cref="PbtTrieNodeGroup.NodeKind.Absent"/>, though the trie has a node there. Only its hash
    /// is recoverable, by folding its two children — which is what the rebuild does, and why nothing
    /// else asks about an inner position.
    /// </remarks>
    /// <param name="position"><inheritdoc cref="this[int]" path="/param[@name='position']"/></param>
    public PbtTrieNodeGroup.NodeKind KindAt(int position)
    {
        if (!NodeGroupMaskEncoding.Contains(_presence, position)) return PbtTrieNodeGroup.NodeKind.Absent;
        if (NodeGroupMaskEncoding.Contains(_stems, position)) return PbtTrieNodeGroup.NodeKind.Stem;

        return PbtLayout.TrieNodeGroupIsBoundaryPosition(position)
            && ChainAt(PbtLayout.TrieNodeGroupBoundarySlot(position))
                ? PbtTrieNodeGroup.NodeKind.Chain
                : PbtTrieNodeGroup.NodeKind.Internal;
    }

    /// <summary>Reads the encoded kind at boundary <paramref name="slot"/> without converting it to a position.</summary>
    /// <param name="slot">The boundary slot in <c>[0, <see cref="IPbtTileLayout.BoundarySlots"/>)</c>.</param>
    public PbtTrieNodeGroup.NodeKind BoundaryKindAt(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        if (slot >= TLayout.BoundarySlots) throw new ArgumentOutOfRangeException(nameof(slot));
        return KindAt(PbtLayout.TrieNodeGroupBoundarySlotPosition(slot));
    }

    /// <summary>Whether boundary <paramref name="slot"/> holds any encoded node.</summary>
    /// <param name="slot">The boundary slot in <c>[0, <see cref="IPbtTileLayout.BoundarySlots"/>)</c>.</param>
    public bool IsBoundaryOccupied(int slot) => BoundaryKindAt(slot) != PbtTrieNodeGroup.NodeKind.Absent;

    /// <summary>
    /// The offset of the entry of the node at <paramref name="position"/>, for a consumer that holds
    /// on to a node rather than reading it out; meaningful only where a node is present.
    /// </summary>
    /// <remarks>
    /// <see cref="PbtLayout.TrieNodeGroupBoundarySlot"/> counts the boundary positions below
    /// <paramref name="position"/>, so it doubles as the slot bound on the runs below it — whether or
    /// not the position is a boundary one itself.
    /// </remarks>
    internal int EntryOffset(int position) =>
        EntriesOffset
        + NodeGroupMaskEncoding.PopCountBelow(_presence, position) * HashLength
        + NodeGroupMaskEncoding.PopCountBelow(_stems, position) * Stem.Length
        + ChainCountBelow(PbtLayout.TrieNodeGroupBoundarySlot(position)) * ChainExtraLength;

    /// <summary>
    /// The bitmaps of the subtree rooted at <paramref name="position"/> and covering
    /// <paramref name="width"/> boundary slots — the shape this group gives it, by position.
    /// </summary>
    internal NodeGroupBitmasks SubtreeBitmaps(int position, int width)
    {
        if (TLayout.PositionMaskWordCount > 2 || TLayout.BoundaryMaskWordCount > 1)
            throw new NotSupportedException("Wide trie node group subtrees require span-based masks");

        UInt128 presence = ReadUInt128(_presence);
        UInt128 stems = ReadUInt128(_stems);
        int firstPosition = PbtLayout.TrieNodeGroupFirstSubtreePosition(position, width);
        UInt128 mask = LowBits(position + 1) & ~LowBits(firstPosition);
        return new NodeGroupBitmasks(presence & mask, stems & mask, ReadLegacyChains() & GatherBoundary(mask));
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
    /// The group's shape at its boundary, gathered down into slot order: bit <c>i</c> of
    /// <see cref="BoundarySlotMasks.Presence"/> is set where boundary slot <c>i</c> holds a node, of
    /// <see cref="BoundarySlotMasks.Stems"/> where that node is a stem and of
    /// <see cref="BoundarySlotMasks.Chains"/> where it is a run. All are zero for the empty group.
    /// </summary>
    /// <remarks>
    /// A boundary slot is never a skipped level, so this reads the same under any
    /// <see cref="PbtGroupFormat"/>; <see cref="Decode"/> keeps the stems and runs bitmaps disjoint
    /// subsets of the presence one, so neither needs masking against it.
    /// </remarks>
    internal BoundarySlotMasks BoundaryShape()
    {
        if (TLayout.BoundaryMaskWordCount > 1)
            throw new NotSupportedException("Wide trie node group boundaries require CopyBoundaryShape");

        Span<ulong> presence = stackalloc ulong[1];
        Span<ulong> stems = stackalloc ulong[1];
        Span<ulong> chains = stackalloc ulong[1];
        CopyBoundaryShape(presence, stems, chains);
        return new BoundarySlotMasks(presence[0], stems[0], chains[0]);
    }

    /// <summary>
    /// Copies the group's boundary shape into caller-owned little-endian 64-bit words: occupied slots,
    /// stem slots, and chain slots respectively. The empty group copies all-zero masks.
    /// </summary>
    /// <param name="presence">Destination for <see cref="IPbtTileLayout.BoundaryMaskWordCount"/> occupied-slot words.</param>
    /// <param name="stems">Destination for <see cref="IPbtTileLayout.BoundaryMaskWordCount"/> stem-slot words.</param>
    /// <param name="chains">Destination for <see cref="IPbtTileLayout.BoundaryMaskWordCount"/> chain-slot words.</param>
    /// <exception cref="ArgumentException">A destination is shorter than the layout's boundary mask.</exception>
    public void CopyBoundaryShape(Span<ulong> presence, Span<ulong> stems, Span<ulong> chains)
    {
        int wordCount = TLayout.BoundaryMaskWordCount;
        if (presence.Length < wordCount) throw new ArgumentException("Destination is shorter than the boundary mask", nameof(presence));
        if (stems.Length < wordCount) throw new ArgumentException("Destination is shorter than the boundary mask", nameof(stems));
        if (chains.Length < wordCount) throw new ArgumentException("Destination is shorter than the boundary mask", nameof(chains));

        presence = presence[..wordCount];
        stems = stems[..wordCount];
        chains = chains[..wordCount];
        presence.Clear();
        stems.Clear();
        chains.Clear();
        if (IsEmpty) return;

        for (int slot = 0; slot < TLayout.BoundarySlots; slot++)
        {
            int position = PbtLayout.TrieNodeGroupBoundarySlotPosition(slot);
            if (NodeGroupMaskEncoding.Contains(_presence, position)) PbtBitset.Set(presence, slot);
            if (NodeGroupMaskEncoding.Contains(_stems, position)) PbtBitset.Set(stems, slot);
            if (ChainAt(slot)) PbtBitset.Set(chains, slot);
        }
    }

    /// <summary>
    /// Validates a non-empty group encoding (bitmaps and implied length) and wraps it as a
    /// read-only view; the returned group borrows <paramref name="data"/> and reads slots on demand.
    /// </summary>
    public static PbtTrieNodeGroup<TLayout> Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < FixedTrailerLength + TLayout.MaskTrailerLength && !TLayout.HasCompactBoundaryMask)
            throw new InvalidDataException($"Trie node group too short: {data.Length} bytes");
        if (TLayout.HasCompactBoundaryMask
            && data.Length < FixedTrailerLength + 2 * TLayout.PositionMaskWordCount * sizeof(ulong) + CompactBitmap256.TopLength)
            throw new InvalidDataException($"Trie node group too short: {data.Length} bytes");

        PbtGroupFormat format = (PbtGroupFormat)data[^1];
        if (format is not (PbtGroupFormat.EveryLevel or PbtGroupFormat.Interleaved or PbtGroupFormat.BoundaryOnly or PbtGroupFormat.Every4Depth))
            throw new InvalidDataException($"Trie node group: unexpected format byte 0x{(byte)format:x2}");

        NodeGroupMaskEncoding.Read<TLayout>(data, out ReadOnlySpan<byte> entries, out ReadOnlySpan<byte> presence,
            out ReadOnlySpan<byte> stems, out ReadOnlySpan<byte> chains, out CompactBitmap256 compactChains, out _);

        int positionBytes = TLayout.PositionMaskWordCount * sizeof(ulong);
        int unusedPositionBits = positionBytes * 8 - TLayout.PositionCount;
        if (unusedPositionBits != 0 && (presence[^1] >> (8 - unusedPositionBits) != 0 || stems[^1] >> (8 - unusedPositionBits) != 0))
            throw new InvalidDataException("Invalid trie node group bitmaps");

        for (int position = 0; position < TLayout.PositionCount; position++)
        {
            bool present = NodeGroupMaskEncoding.Contains(presence, position);
            bool isStem = NodeGroupMaskEncoding.Contains(stems, position);
            if (isStem && !present) throw new InvalidDataException("Invalid trie node group bitmaps");
            if (present && !isStem && PbtLayout.TrieNodeGroupIsSkippedPosition(format, position))
                throw new InvalidDataException($"{format} trie node group holds an internal node at a skipped level");
        }

        int chainCount = 0;
        for (int slot = 0; slot < TLayout.BoundarySlots; slot++)
        {
            bool isChain = TLayout.HasCompactBoundaryMask
                ? compactChains.IsSet(slot)
                : NodeGroupMaskEncoding.Contains(chains, slot);
            if (!isChain) continue;
            chainCount++;
            int position = PbtLayout.TrieNodeGroupBoundarySlotPosition(slot);
            if (!NodeGroupMaskEncoding.Contains(presence, position) || NodeGroupMaskEncoding.Contains(stems, position))
                throw new InvalidDataException("Invalid trie node group run bitmap");
        }

        if (NodeGroupMaskEncoding.Contains(presence, TLayout.RootPosition)
            && !NodeGroupMaskEncoding.Contains(stems, TLayout.RootPosition))
            throw new InvalidDataException("Trie node group stores an internal node at the root position");

        int expectedEntriesLength = NodeGroupMaskEncoding.PopCount(presence) * HashLength
            + NodeGroupMaskEncoding.PopCount(stems) * Stem.Length
            + chainCount * ChainExtraLength;
        if (entries.Length != expectedEntriesLength)
            throw new InvalidDataException($"Trie node group length {data.Length} does not match its bitmaps");

        return new PbtTrieNodeGroup<TLayout>(data, format, presence, stems, chains, compactChains);
    }

    private bool ChainAt(int slot) => TLayout.HasCompactBoundaryMask
        ? _compactChains.IsSet(slot)
        : NodeGroupMaskEncoding.Contains(_chains, slot);

    private int ChainCountBelow(int slot)
    {
        int count = 0;
        for (int current = 0; current < slot; current++) if (ChainAt(current)) count++;
        return count;
    }

    private static UInt128 ReadUInt128(ReadOnlySpan<byte> bytes)
    {
        UInt128 value = 0;
        for (int index = bytes.Length - 1; index >= 0; index--) value = value << 8 | bytes[index];
        return value;
    }

    private static UInt128 LowBits(int count) => count == 128 ? UInt128.MaxValue : (UInt128.One << count) - 1;

    private static ulong GatherBoundary(UInt128 positions) => PbtLayout.GatherBoundary(positions, TLayout.BoundarySlots);

    private ulong ReadLegacyChains()
    {
        ulong value = 0;
        for (int index = _chains.Length - 1; index >= 0; index--) value = value << 8 | _chains[index];
        return value;
    }
}
