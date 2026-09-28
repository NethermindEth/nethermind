// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Encodes and reads a four-level node group's canonical node payload.</summary>
/// <remarks>
/// The physical payload starts with version byte 6, followed by entries and a variable-size footer.
/// Entries are complete branch encodings in ascending post-order position order, with no padding or
/// separators; leaves are inlined in their parent branch's trailer (see <see cref="PbtNodeCodec"/>),
/// so the only leaf entry is the root of a single-leaf tree. The footer contains one little-endian
/// unsigned 16-bit offset per physically stored node, in the same order, followed by a little-endian
/// unsigned 32-bit availability bitmap, one little-endian descendant size per set bit of the closing
/// little-endian unsigned 16-bit descendant mask, in ascending boundary-slot order, the width byte of
/// those sizes when the mask is nonzero, and that mask. Every size takes the width, 1–6 bytes, of the
/// largest one, since the subtrees below a group are of similar size. Bit <c>n</c> of the mask is set exactly when the groups physically stored below boundary
/// slot <c>n</c> have a nonzero summed payload length, which is the size recorded for the slot. Keeping
/// the sizes per slot lets a group created between existing groups take its descendants' size from its
/// parent instead of reading them. Offsets are relative to the beginning of the entries
/// section. The first offset is zero, and subsequent offsets strictly increase; a node ends at the next
/// offset or the footer's beginning. Position 30 is reserved for the root and may only be present in
/// the depth-zero root group. The group key is deliberately kept outside this payload. Prefixless
/// branches at relative depths 1–3 are omitted when neither child is an inline leaf: only their
/// descendants are stored. Availability describes physical entries.
/// </remarks>
public static class PbtNodeGroupCodec
{
    internal const int HeaderLength = 1;
    internal static ReadOnlySpan<byte> Header => "\x06"u8;

    /// <summary>The number of positions represented by the offset table.</summary>
    public const int PositionCount = PbtFourLevelGroupGeometry.PositionCount;

    /// <summary>The number of boundary slots a group records descendant sizes for.</summary>
    public const int DescendantSlots = PbtFourLevelGroupGeometry.BoundarySlots;

    /// <summary>The number of bytes in the widest descendant-size field.</summary>
    public const int MaxDescendantBytesLength = 6;

    private const int DescendantWidthLength = 1;

    /// <summary>The number of bytes in the descendant mask that ends the footer.</summary>
    public const int DescendantMaskLength = sizeof(ushort);

    /// <summary>The maximum number of bytes in the packed offset, availability and descendant-size footer.</summary>
    public const int MaxTrailerLength = PositionCount * sizeof(ushort) + sizeof(uint) + DescendantSlots * MaxDescendantBytesLength + DescendantWidthLength + DescendantMaskLength;

    /// <summary>The largest descendant size a 48-bit field can hold.</summary>
    public const long MaxDescendantBytes = (1L << (8 * MaxDescendantBytesLength)) - 1;

    private const int MaxOffset = ushort.MaxValue;

    /// <summary>The largest payload a group can encode: header, a full entries section, and the widest trailer.</summary>
    public const int MaxPayloadLength = HeaderLength + MaxOffset + MaxTrailerLength;
    private const uint ReservedRootBit = 1u << PbtFourLevelGroupGeometry.RootPosition;
    private const uint AllowedPositionBits = (1u << PositionCount) - 1;

    /// <summary>Checks a payload's header, footer length, availability bits and descendant sizes without parsing its nodes.</summary>
    /// <returns>The availability bitmap.</returns>
    internal static uint ValidateFraming(int groupDepth, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength || !payload[..HeaderLength].SequenceEqual(Header))
            throw new InvalidDataException("Unsupported or missing PBT node group format header.");
        if (payload.Length < HeaderLength + sizeof(uint) + DescendantMaskLength) throw new InvalidDataException("Truncated PBT node group footer.");
        ushort descendantMask = ReadDescendantMask(payload);
        if (descendantMask != 0)
        {
            if (payload.Length < HeaderLength + sizeof(uint) + DescendantWidthLength + DescendantMaskLength) throw new InvalidDataException("Truncated PBT node group footer.");
            if (ReadDescendantWidth(payload) is < 1 or > MaxDescendantBytesLength) throw new InvalidDataException("Invalid PBT node group descendant-size width.");
        }
        if (payload.Length < HeaderLength + sizeof(uint) + DescendantsLength(payload)) throw new InvalidDataException("Truncated PBT node group footer.");
        long widest = 0;
        for (int slot = 0; slot < DescendantSlots; slot++)
        {
            if ((descendantMask & (1 << slot)) == 0) continue;
            long slotBytes = ReadDescendantBytes(payload, descendantMask, slot);
            if (slotBytes == 0) throw new InvalidDataException("PBT node group descendant mask marks an empty slot.");
            widest = Math.Max(widest, slotBytes);
        }
        if (ByteWidth(widest) != ReadDescendantWidth(payload)) throw new InvalidDataException("PBT node group descendant sizes are wider than the largest one.");
        payload = payload[HeaderLength..];
        uint availability = ReadAvailability(payload);
        if (availability == 0 || (availability & ~AllowedPositionBits) != 0 || (groupDepth != 0 && (availability & ReservedRootBit) != 0))
            throw new InvalidDataException("Invalid PBT node group availability bits.");
        if (payload.Length < GetTrailerLength(availability, payload)) throw new InvalidDataException("Truncated PBT node group offset table.");
        return availability;
    }

    /// <summary>Checks every node of a payload on top of <see cref="ValidateFraming"/>: the offset table delimits positive-length nodes and each node is structurally exact.</summary>
    internal static void ValidateNodes(scoped in PbtTraversalPath path, ReadOnlySpan<byte> payload)
    {
        uint availability = ValidateFraming(path.BitDepth, payload);
        payload = payload[HeaderLength..];
        int entriesLength = payload.Length - GetTrailerLength(availability, payload);
        if (entriesLength > ushort.MaxValue) throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
        ReadOnlySpan<byte> footer = payload[entriesLength..];

        Span<int> offsets = stackalloc int[PositionCount];
        int previousOffset = -1;
        bool foundPresent = false;
        int offsetIndex = 0;
        for (int position = 0; position < PositionCount; position++)
        {
            if ((availability & (1u << position)) == 0) continue;
            ushort encodedOffset = BinaryPrimitives.ReadUInt16LittleEndian(footer[offsetIndex..]);
            offsetIndex += sizeof(ushort);
            if (!foundPresent && encodedOffset != 0) throw new InvalidDataException("The first PBT node offset must be zero.");
            if (foundPresent && encodedOffset <= previousOffset) throw new InvalidDataException("PBT node offsets must strictly increase.");
            if (encodedOffset >= entriesLength) throw new InvalidDataException("PBT node offset is outside the entries section.");
            offsets[position] = encodedOffset;
            previousOffset = encodedOffset;
            foundPresent = true;
        }

        int nextOffset = entriesLength;
        for (int position = PositionCount - 1; position >= 0; position--)
        {
            if ((availability & (1u << position)) == 0) continue;
            int start = offsets[position];
            if (nextOffset <= start) throw new InvalidDataException("PBT node offsets do not delimit a positive-length node.");
            ReadOnlySpan<byte> encoding = payload[start..nextOffset];
            try
            {
                PbtNodeCodec.ValidateExact(encoding);
                if (encoding[0] == 0 && position != PbtFourLevelGroupGeometry.RootPosition)
                    throw new InvalidDataException("A PBT leaf entry is only valid as the tree root.");
                PbtNodeGroupReader.ValidateLeafPath(path, position, encoding);
            }
            catch (InvalidDataException exception) { throw new InvalidDataException("Invalid PBT node in group.", exception); }
            nextOffset = start;
        }
    }

    /// <summary>Debug-build guard for a payload entering or leaving a store; readers on the update path trust stored payloads.</summary>
    [Conditional("DEBUG")]
    internal static void DebugValidateNodes<TPath>(TPath groupKey, ReadOnlySpan<byte> payload) where TPath : struct, IPbtNodePath<TPath> =>
        ValidateNodes(PbtTraversalPath.FromPath(stackalloc byte[PbtStorageTreeKey.MaxLength], groupKey), payload);

    /// <summary>Reads the descendant mask that ends a payload, without validating anything else.</summary>
    public static ushort ReadDescendantMask(ReadOnlySpan<byte> payload) => BinaryPrimitives.ReadUInt16LittleEndian(payload[^DescendantMaskLength..]);

    /// <summary>Reads the availability bitmap of a payload whose header has been stripped, without validating anything else.</summary>
    internal static uint ReadAvailability(ReadOnlySpan<byte> payload) =>
        BinaryPrimitives.ReadUInt32LittleEndian(payload[^(sizeof(uint) + DescendantsLength(payload))..]);

    internal static long ReadDescendantBytes(ReadOnlySpan<byte> payload, ushort descendantMask, int slot)
    {
        int width = ReadDescendantWidth(payload);
        int fieldsAfter = BitOperations.PopCount((uint)(descendantMask >> (slot + 1)));
        ReadOnlySpan<byte> field = payload[^(DescendantWidthLength + DescendantMaskLength + (fieldsAfter + 1) * width)..][..width];
        long slotBytes = 0;
        for (int index = width - 1; index >= 0; index--) slotBytes = slotBytes << 8 | field[index];
        return slotBytes;
    }

    private static int ReadDescendantWidth(ReadOnlySpan<byte> payload) =>
        ReadDescendantMask(payload) == 0 ? 0 : payload[^(DescendantWidthLength + DescendantMaskLength)];

    private static int DescendantsLength(ushort descendantMask, int width) =>
        descendantMask == 0 ? DescendantMaskLength : BitOperations.PopCount(descendantMask) * width + DescendantWidthLength + DescendantMaskLength;

    private static int DescendantsLength(ReadOnlySpan<byte> payload) => DescendantsLength(ReadDescendantMask(payload), ReadDescendantWidth(payload));

    /// <summary>The width every descendant-size field takes: the byte count of the largest masked size, or zero for none.</summary>
    private static int DescendantWidth(ReadOnlySpan<long> descendantBytes, ushort descendantMask)
    {
        long widest = 0;
        for (uint remaining = descendantMask; remaining != 0; remaining &= remaining - 1)
            widest = Math.Max(widest, descendantBytes[BitOperations.TrailingZeroCount(remaining)]);
        return ByteWidth(widest);
    }

    private static int ByteWidth(long value) => (64 - BitOperations.LeadingZeroCount((ulong)value) + 7) / 8;

    private static int OffsetsAndAvailabilityLength(uint availability) => BitOperations.PopCount(availability) * sizeof(ushort) + sizeof(uint);

    /// <summary>The footer length of a group being written.</summary>
    /// <param name="descendantMask">The <see cref="DescendantMask(ReadOnlySpan{long}, ushort)"/> of <paramref name="descendantBytes"/>.</param>
    internal static int GetTrailerLength(uint availability, ushort descendantMask, ReadOnlySpan<long> descendantBytes) =>
        OffsetsAndAvailabilityLength(availability) + DescendantsLength(descendantMask, DescendantWidth(descendantBytes, descendantMask));

    /// <summary>The footer length of a stored payload, with or without its header.</summary>
    internal static int GetTrailerLength(uint availability, ReadOnlySpan<byte> payload) =>
        OffsetsAndAvailabilityLength(availability) + DescendantsLength(payload);

    /// <summary>Validates per-slot descendant sizes and returns the mask of nonzero slots; an empty span means no descendants.</summary>
    /// <param name="candidateSlots">The slots that may be nonzero; every other slot is known to be zero and is not read.</param>
    internal static ushort DescendantMask(ReadOnlySpan<long> descendantBytes, ushort candidateSlots)
    {
        if (descendantBytes.IsEmpty) return 0;
        if (descendantBytes.Length != DescendantSlots) throw new ArgumentException("One size per boundary slot is required.", nameof(descendantBytes));
        ushort descendantMask = 0;
        for (uint remaining = candidateSlots; remaining != 0; remaining &= remaining - 1)
        {
            int slot = BitOperations.TrailingZeroCount(remaining);
            long slotBytes = descendantBytes[slot];
            ArgumentOutOfRangeException.ThrowIfNegative(slotBytes, nameof(descendantBytes));
            if (slotBytes > MaxDescendantBytes) throw new InvalidDataException("PBT node group descendant size exceeds the uint48 limit.");
            if (slotBytes != 0) descendantMask |= (ushort)(1 << slot);
        }
        return descendantMask;
    }

    /// <param name="descendantMask">The <see cref="DescendantMask(ReadOnlySpan{long}, ushort)"/> of <paramref name="descendantBytes"/>.</param>
    internal static void WriteFooter(Span<byte> footer, ReadOnlySpan<ushort> offsets, uint availability, ushort descendantMask, ReadOnlySpan<long> descendantBytes)
    {
        int offsetIndex = 0;
        for (uint remaining = availability; remaining != 0; remaining &= remaining - 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(footer[offsetIndex..], offsets[BitOperations.TrailingZeroCount(remaining)]);
            offsetIndex += sizeof(ushort);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(footer[offsetIndex..], availability);
        Span<byte> field = footer[(offsetIndex + sizeof(uint))..];
        int width = DescendantWidth(descendantBytes, descendantMask);
        for (uint remaining = descendantMask; remaining != 0; remaining &= remaining - 1)
        {
            long slotBytes = descendantBytes[BitOperations.TrailingZeroCount(remaining)];
            for (int index = 0; index < width; index++) field[index] = (byte)(slotBytes >> (8 * index));
            field = field[width..];
        }
        if (descendantMask != 0)
        {
            field[0] = (byte)width;
            field = field[DescendantWidthLength..];
        }
        BinaryPrimitives.WriteUInt16LittleEndian(field, descendantMask);
    }

    private static readonly int PrefixlessBranchLength = PbtNodeCodec.BranchLength(0, 0, 0);

    /// <summary>A prefixless interior branch without inline leaves is reconstructed from its children, so it need not be stored.</summary>
    /// <remarks>Relative depth 1 is width 8, depth 2 width 4 and depth 3 width 2 in <see cref="PbtFourLevelGroupGeometry.WidthOf"/>.</remarks>
    internal static bool ShouldOmit(int position, ReadOnlySpan<byte> encoding) =>
        PbtFourLevelGroupGeometry.WidthOf(position) is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots
        && encoding.Length == PrefixlessBranchLength && encoding[0] == 1 && encoding[1] == 0 && encoding[2] == 0
        && encoding[PrefixlessBranchLength - 2] == 0 && encoding[PrefixlessBranchLength - 1] == 0;
}

/// <summary>Provides a validated, allocation-free view over a borrowed node-group payload.</summary>
public readonly ref struct PbtNodeGroupReader
{
    private readonly int _groupDepth;
    private readonly ReadOnlySpan<byte> _payload;
    private readonly int _entriesLength;
    private readonly uint _availability;
    private readonly ushort _descendantMask;
    private readonly bool _initialized;

    /// <summary>Validates and borrows a complete node-group payload.</summary>
    /// <remarks>The path is used only for validation; advancing the cursor cannot change this reader or its enumerators.
    /// The payload must remain valid and immutable for the lifetime of the reader and its enumerators.</remarks>
    public PbtNodeGroupReader(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload) : this(path, payload, validated: false) { }

    /// <summary>Borrows a payload a store has already validated, reading only its footer's fixed fields.</summary>
    internal static PbtNodeGroupReader FromValidated(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload) => new(path, payload, validated: true);

    // Nodes and descendant sizes are located on access by ranking their bit in the availability or descendant mask,
    // since a traversal reads only a few positions of each group it passes through.
    private PbtNodeGroupReader(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload, bool validated)
    {
        int groupDepth = path.BitDepth;
        if (!validated) PbtNodeGroupCodec.ValidateNodes(path, payload);
        Debug.Assert(PbtFourLevelGroupGeometry.IsGroupDepth(groupDepth));
        payload = payload[PbtNodeGroupCodec.HeaderLength..];
        ushort descendantMask = PbtNodeGroupCodec.ReadDescendantMask(payload);
        uint availability = PbtNodeGroupCodec.ReadAvailability(payload);

        _groupDepth = groupDepth;
        _payload = payload;
        _entriesLength = payload.Length - PbtNodeGroupCodec.GetTrailerLength(availability, payload);
        _availability = availability;
        _descendantMask = descendantMask;
        _initialized = true;
    }

    /// <summary>Gets the summed payload lengths of the groups physically stored below boundary slot <paramref name="slot"/>.</summary>
    public long DescendantBytes(int slot)
    {
        EnsureInitialized();
        if ((uint)slot >= PbtNodeGroupCodec.DescendantSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        return (_descendantMask & (1 << slot)) == 0 ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(_payload, _descendantMask, slot);
    }
    /// <summary>Gets the number of nodes in this group.</summary>
    public int Count { get { EnsureInitialized(); return BitOperations.PopCount(_availability); } }
    /// <summary>Gets a borrowed canonical node encoding at a position.</summary>
    public ReadOnlySpan<byte> GetNode(int position)
    {
        ValidatePosition(position);
        return (_availability & (1u << position)) == 0 ? [] : NodeAt(position);
    }
    /// <summary>Attempts to get a borrowed canonical node encoding.</summary>
    public bool TryGetNode(int position, out ReadOnlySpan<byte> encoding)
    {
        ValidatePosition(position);
        if ((_availability & (1u << position)) == 0) { encoding = default; return false; }
        encoding = NodeAt(position);
        return true;
    }

    private ReadOnlySpan<byte> NodeAt(int position)
    {
        ReadOnlySpan<byte> offsets = _payload[(_entriesLength + BitOperations.PopCount(_availability & ((1u << position) - 1)) * sizeof(ushort))..];
        int start = BinaryPrimitives.ReadUInt16LittleEndian(offsets);
        int end = _availability >> (position + 1) == 0 ? _entriesLength : BinaryPrimitives.ReadUInt16LittleEndian(offsets[sizeof(ushort)..]);
        return _payload[start..end];
    }

    /// <summary>Gets an allocation-free positional enumerator.</summary>
    public Enumerator EnumerateNodes()
    {
        EnsureInitialized();
        return new(this);
    }

    private void ValidatePosition(int position)
    {
        EnsureInitialized();
        if ((uint)position >= PbtNodeGroupCodec.PositionCount || (position == PbtFourLevelGroupGeometry.RootPosition && _groupDepth != 0))
            throw new ArgumentOutOfRangeException(nameof(position));
    }
    private void EnsureInitialized() { if (!_initialized) throw new InvalidOperationException("The PBT node-group reader is not initialized."); }
    /// <summary>Checks that a branch's inline leaf keys lie below the branch's position.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void ValidateLeafPath(scoped in PbtTraversalPath groupKey, int position, ReadOnlySpan<byte> encoding)
    {
        if (encoding[0] == 0)
        {
            if (position != PbtFourLevelGroupGeometry.RootPosition) throw new InvalidDataException("A PBT leaf entry is only valid as the tree root.");
            return;
        }
        if (position == PbtFourLevelGroupGeometry.RootPosition) return;
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
        Span<byte> directions = stackalloc byte[PbtFourLevelGroupGeometry.LevelsPerGroup];
        int relativeDepth = RelativeDirections(position, directions);
        ValidateInlineLeafPath(groupKey, node.LeftKey, directions[..relativeDepth]);
        ValidateInlineLeafPath(groupKey, node.RightKey, directions[..relativeDepth]);
    }

    private static void ValidateInlineLeafPath(scoped in PbtTraversalPath groupKey, ReadOnlySpan<byte> key, ReadOnlySpan<byte> directions)
    {
        if (key.IsEmpty) return;
        int relativeDepth = directions.Length;
        // The leaf hangs at least one level below the branch.
        int requiredDepth = checked(groupKey.BitDepth + relativeDepth + 1);
        if (key.Length * 8 < requiredDepth) throw new InvalidDataException("PBT leaf does not match its group position.");
        int completeBytes = groupKey.BitDepth >> 3;
        if (!groupKey.Bytes[..completeBytes].SequenceEqual(key[..completeBytes]))
            throw new InvalidDataException("PBT leaf does not match its group position.");

        // Four-level group alignment keeps the group tail and relative path in one byte.
        int groupTailBits = groupKey.BitDepth & 7;
        int expectedTail = groupTailBits == 0 ? 0 : groupKey.Bytes[completeBytes];
        for (int index = 0; index < relativeDepth; index++)
            expectedTail |= directions[index] << (7 - groupTailBits - index);
        int tailMask = 0xFF << (8 - groupTailBits - relativeDepth);
        if (((key[completeBytes] ^ expectedTail) & tailMask) != 0)
            throw new InvalidDataException("PBT leaf does not match its group position.");
    }
    private static int RelativeDirections(int position, Span<byte> directions)
    {
        int currentPosition = PbtFourLevelGroupGeometry.RootPosition, width = PbtFourLevelGroupGeometry.BoundarySlots, depth = 0;
        while (depth < PbtFourLevelGroupGeometry.LevelsPerGroup && position != currentPosition)
        {
            int halfWidth = width / 2, leftPosition = currentPosition - width, rightPosition = currentPosition - 1;
            int leftFirst = leftPosition - 2 * halfWidth + 2, rightFirst = rightPosition - 2 * halfWidth + 2;
            if (position >= leftFirst && position <= leftPosition) { directions[depth++] = 0; currentPosition = leftPosition; width = halfWidth; }
            else if (position >= rightFirst && position <= rightPosition) { directions[depth++] = 1; currentPosition = rightPosition; width = halfWidth; }
            else throw new InvalidDataException("Invalid PBT node position.");
        }
        if (position != currentPosition || depth == 0) throw new InvalidDataException("Invalid PBT node position.");
        return depth;
    }

    /// <summary>Enumerates present positions without allocating.</summary>
    public ref struct Enumerator
    {
        private readonly PbtNodeGroupReader _reader;
        private int _position;
        internal Enumerator(PbtNodeGroupReader reader) { _reader = reader; _position = -1; Current = default; CurrentPosition = -1; }
        /// <summary>Gets the current node encoding.</summary>
        public ReadOnlySpan<byte> Current { get; private set; }
        /// <summary>Gets the current post-order position.</summary>
        public int CurrentPosition { get; private set; }
        /// <summary>Advances to the next present node.</summary>
        public bool MoveNext()
        {
            _reader.EnsureInitialized();
            while (++_position < PbtNodeGroupCodec.PositionCount)
            {
                if ((_reader._availability & (1u << _position)) == 0) continue;
                CurrentPosition = _position; Current = _reader.GetNode(_position); return true;
            }
            Current = default; CurrentPosition = -1; return false;
        }
    }
}
