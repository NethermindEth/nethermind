// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>Encodes and reads a four-level node group's canonical node payload.</summary>
/// <remarks>
/// The physical payload starts with version byte 5, followed by entries and a variable-size footer.
/// Entries are complete branch encodings in ascending post-order position order, with no padding or
/// separators; leaves are inlined in their parent branch's trailer (see <see cref="PbtNodeCodec"/>),
/// so the only leaf entry is the root of a single-leaf tree. The footer contains one little-endian
/// unsigned 16-bit offset per physically stored node, in the same order, followed by a little-endian
/// unsigned 32-bit availability bitmap, one little-endian unsigned 48-bit descendant size per set bit
/// of the closing little-endian unsigned 16-bit descendant mask, in ascending boundary-slot order, and
/// that mask. Bit <c>n</c> of the mask is set exactly when the groups physically stored below boundary
/// slot <c>n</c> have a nonzero summed payload length, which is the size recorded for the slot. Keeping
/// the sizes per slot lets a group created between existing groups take its descendants' size from its
/// parent instead of reading them. Offsets are relative to the beginning of the entries
/// section. The first offset is zero, and subsequent offsets strictly increase; a node ends at the next
/// offset or the footer's beginning. Position 30 is reserved for the root and may only be present in
/// the depth-zero root group. The group key is deliberately kept outside this payload. Prefixless
/// branches at relative depths 1–3 may be omitted when neither child is an inline leaf: only their
/// descendants need be stored (see <see cref="PbtPrefixlessBranchOmission"/>). Availability describes physical entries.
/// </remarks>
public static class PbtNodeGroupCodec
{
    internal const int HeaderLength = 1;
    internal static ReadOnlySpan<byte> Header => "\x05"u8;

    /// <summary>The number of positions represented by the offset table.</summary>
    public const int PositionCount = PbtFourLevelGroupGeometry.PositionCount;

    /// <summary>The number of boundary slots a group records descendant sizes for.</summary>
    public const int DescendantSlots = PbtFourLevelGroupGeometry.BoundarySlots;

    /// <summary>The number of bytes in one descendant-size field.</summary>
    public const int DescendantBytesLength = 6;

    /// <summary>The number of bytes in the descendant mask that ends the footer.</summary>
    public const int DescendantMaskLength = sizeof(ushort);

    /// <summary>The maximum number of bytes in the packed offset, availability and descendant-size footer.</summary>
    public const int MaxTrailerLength = PositionCount * sizeof(ushort) + sizeof(uint) + DescendantSlots * DescendantBytesLength + DescendantMaskLength;

    /// <summary>The largest descendant size a 48-bit field can hold.</summary>
    public const long MaxDescendantBytes = (1L << (8 * DescendantBytesLength)) - 1;

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
        int descendantsLength = DescendantsLength(descendantMask);
        if (payload.Length < HeaderLength + sizeof(uint) + descendantsLength) throw new InvalidDataException("Truncated PBT node group footer.");
        for (int slot = 0; slot < DescendantSlots; slot++)
            if ((descendantMask & (1 << slot)) != 0 && ReadDescendantBytes(payload, descendantMask, slot) == 0)
                throw new InvalidDataException("PBT node group descendant mask marks an empty slot.");
        payload = payload[HeaderLength..];
        uint availability = BinaryPrimitives.ReadUInt32LittleEndian(payload[^(sizeof(uint) + descendantsLength)..]);
        if (availability == 0 || (availability & ~AllowedPositionBits) != 0 || (groupDepth != 0 && (availability & ReservedRootBit) != 0))
            throw new InvalidDataException("Invalid PBT node group availability bits.");
        if (payload.Length < GetTrailerLength(availability, descendantMask)) throw new InvalidDataException("Truncated PBT node group offset table.");
        return availability;
    }

    /// <summary>Reads the descendant mask that ends a payload, without validating anything else.</summary>
    public static ushort ReadDescendantMask(ReadOnlySpan<byte> payload) => BinaryPrimitives.ReadUInt16LittleEndian(payload[^DescendantMaskLength..]);

    /// <summary>Reads the descendant size of each boundary slot into <paramref name="descendantBytes"/>, without validating anything else.</summary>
    /// <param name="descendantBytes">Receives one size per boundary slot; slots without descendants read as zero.</param>
    public static void ReadDescendantBytes(ReadOnlySpan<byte> payload, Span<long> descendantBytes)
    {
        if (descendantBytes.Length != DescendantSlots) throw new ArgumentException("One size per boundary slot is required.", nameof(descendantBytes));
        ushort descendantMask = ReadDescendantMask(payload);
        for (int slot = 0; slot < DescendantSlots; slot++)
            descendantBytes[slot] = (descendantMask & (1 << slot)) == 0 ? 0 : ReadDescendantBytes(payload, descendantMask, slot);
    }

    private static long ReadDescendantBytes(ReadOnlySpan<byte> payload, ushort descendantMask, int slot)
    {
        int fieldsAfter = BitOperations.PopCount((uint)(descendantMask >> (slot + 1)));
        ReadOnlySpan<byte> field = payload[^(DescendantMaskLength + (fieldsAfter + 1) * DescendantBytesLength)..];
        return BinaryPrimitives.ReadUInt32LittleEndian(field) | ((long)BinaryPrimitives.ReadUInt16LittleEndian(field[sizeof(uint)..]) << 32);
    }

    private static int DescendantsLength(ushort descendantMask) => BitOperations.PopCount(descendantMask) * DescendantBytesLength + DescendantMaskLength;

    internal static int GetTrailerLength(uint availability, ushort descendantMask) =>
        BitOperations.PopCount(availability) * sizeof(ushort) + sizeof(uint) + DescendantsLength(descendantMask);

    /// <summary>Validates per-slot descendant sizes and returns the mask of nonzero slots; an empty span means no descendants.</summary>
    internal static ushort DescendantMask(ReadOnlySpan<long> descendantBytes)
    {
        if (descendantBytes.IsEmpty) return 0;
        if (descendantBytes.Length != DescendantSlots) throw new ArgumentException("One size per boundary slot is required.", nameof(descendantBytes));
        ushort descendantMask = 0;
        for (int slot = 0; slot < DescendantSlots; slot++)
        {
            long slotBytes = descendantBytes[slot];
            ArgumentOutOfRangeException.ThrowIfNegative(slotBytes, nameof(descendantBytes));
            if (slotBytes > MaxDescendantBytes) throw new InvalidDataException("PBT node group descendant size exceeds the uint48 limit.");
            if (slotBytes != 0) descendantMask |= (ushort)(1 << slot);
        }
        return descendantMask;
    }

    internal static void WriteFooter(Span<byte> footer, ReadOnlySpan<ushort> offsets, uint availability, ReadOnlySpan<long> descendantBytes)
    {
        ushort descendantMask = DescendantMask(descendantBytes);
        int offsetIndex = 0;
        for (int position = 0; position < PositionCount; position++)
        {
            if ((availability & (1u << position)) == 0) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(footer[offsetIndex..], offsets[position]);
            offsetIndex += sizeof(ushort);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(footer[offsetIndex..], availability);
        Span<byte> field = footer[(offsetIndex + sizeof(uint))..];
        for (int slot = 0; slot < DescendantSlots; slot++)
        {
            if ((descendantMask & (1 << slot)) == 0) continue;
            long slotBytes = descendantBytes[slot];
            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)slotBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(field[sizeof(uint)..], (ushort)(slotBytes >> 32));
            field = field[DescendantBytesLength..];
        }
        BinaryPrimitives.WriteUInt16LittleEndian(field, descendantMask);
    }

    private static readonly int PrefixlessBranchLength = PbtNodeCodec.BranchLength(0, 0, 0);

    /// <summary>A prefixless interior branch without inline leaves is reconstructed from its children, so it need not be stored.</summary>
    /// <remarks>Relative depth 1 is width 8, depth 2 width 4 and depth 3 width 2 in <see cref="PbtFourLevelGroupGeometry.WidthOf"/>.</remarks>
    internal static bool ShouldOmit(PbtPrefixlessBranchOmission omission, int position, ReadOnlySpan<byte> encoding) =>
        omission switch
        {
            PbtPrefixlessBranchOmission.Interior => PbtFourLevelGroupGeometry.WidthOf(position) is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots,
            PbtPrefixlessBranchOmission.OddLevels => PbtFourLevelGroupGeometry.WidthOf(position) is 2 or 8,
            _ => false,
        }
        && encoding.Length == PrefixlessBranchLength && encoding[0] == 1 && encoding[1] == 0 && encoding[2] == 0
        && encoding[PrefixlessBranchLength - 2] == 0 && encoding[PrefixlessBranchLength - 1] == 0;
}

/// <summary>Provides a validated, allocation-free view over a borrowed node-group payload.</summary>
public readonly ref struct PbtNodeGroupReader
{
    private readonly int _groupDepth;
    private readonly int _payloadLength;
    private readonly ReadOnlySpan<byte> _payload;
    private readonly OffsetBuffer _offsets;
    private readonly LengthBuffer _lengths;
    private readonly uint _availability;
    private readonly DescendantBuffer _descendantBytes;
    private readonly bool _initialized;

    /// <summary>Validates and borrows a complete node-group payload.</summary>
    /// <remarks>The path is used only for validation; advancing the cursor cannot change this reader or its enumerators.
    /// The payload must remain valid and immutable for the lifetime of the reader and its enumerators.</remarks>
    [SkipLocalsInit]
    public PbtNodeGroupReader(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload)
    {
        int groupDepth = path.BitDepth;
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupDepth)) throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(path));
        uint availability = PbtNodeGroupCodec.ValidateFraming(groupDepth, payload);
        DescendantBuffer descendantBytes = default;
        PbtNodeGroupCodec.ReadDescendantBytes(payload, descendantBytes);
        int payloadLength = payload.Length;
        payload = payload[PbtNodeGroupCodec.HeaderLength..];
        int trailerLength = PbtNodeGroupCodec.GetTrailerLength(availability, PbtNodeGroupCodec.ReadDescendantMask(payload));
        int entriesLength = payload.Length - trailerLength;
        if (entriesLength > ushort.MaxValue) throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
        ReadOnlySpan<byte> footer = payload[entriesLength..];

        OffsetBuffer offsets = default;
        LengthBuffer lengths = default;
        int previousOffset = -1;
        bool foundPresent = false;
        int offsetIndex = 0;
        for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
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
        for (int position = PbtNodeGroupCodec.PositionCount - 1; position >= 0; position--)
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
                ValidateLeafPath(path, position, encoding);
            }
            catch (InvalidDataException exception) { throw new InvalidDataException("Invalid PBT node in group.", exception); }
            lengths[position] = nextOffset - start;
            nextOffset = start;
        }

        _groupDepth = groupDepth;
        _payload = payload;
        _offsets = offsets;
        _lengths = lengths;
        _availability = availability;
        _descendantBytes = descendantBytes;
        _payloadLength = payloadLength;
        _initialized = true;
    }

    /// <summary>Gets the summed payload lengths of the groups physically stored below boundary slot <paramref name="slot"/>.</summary>
    public long DescendantBytes(int slot)
    {
        EnsureInitialized();
        if ((uint)slot >= PbtNodeGroupCodec.DescendantSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        return _descendantBytes[slot];
    }
    /// <summary>Gets the number of nodes in this group.</summary>
    public int Count { get { EnsureInitialized(); return BitOperations.PopCount(_availability); } }
    /// <summary>Gets a borrowed canonical node encoding at a position.</summary>
    public ReadOnlySpan<byte> GetNode(int position)
    {
        ValidatePosition(position);
        return (_availability & (1u << position)) == 0 ? [] : _payload.Slice(_offsets[position], _lengths[position]);
    }
    /// <summary>Gets a borrowed canonical node encoding at a position.</summary>
    public ReadOnlySpan<byte> this[int position] => GetNode(position);
    /// <summary>Attempts to get a borrowed canonical node encoding.</summary>
    public bool TryGetNode(int position, out ReadOnlySpan<byte> encoding)
    {
        ValidatePosition(position);
        if ((_availability & (1u << position)) == 0) { encoding = default; return false; }
        encoding = _payload.Slice(_offsets[position], _lengths[position]);
        return true;
    }

    internal bool TryGetNodeRange(int position, out int offset, out int length)
    {
        ValidatePosition(position);
        if ((_availability & (1u << position)) == 0) { offset = 0; length = 0; return false; }
        offset = _offsets[position] + PbtNodeGroupCodec.HeaderLength;
        length = _lengths[position];
        return true;
    }

    /// <summary>Gets an allocation-free positional enumerator.</summary>
    public Enumerator GetEnumerator()
    {
        EnsureInitialized();
        return new(this);
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
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void ValidateLeafPath<TPath>(TPath groupKey, int position, ReadOnlySpan<byte> encoding) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath path = PbtTraversalPath.FromPath(stackalloc byte[PbtStorageTreeKey.MaxLength], groupKey);
        ValidateLeafPath(path, position, encoding);
    }

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
    [InlineArray(PbtNodeGroupCodec.PositionCount)] private struct OffsetBuffer { private ushort _element; }
    [InlineArray(PbtNodeGroupCodec.PositionCount)] private struct LengthBuffer { private int _element; }
    [InlineArray(PbtNodeGroupCodec.DescendantSlots)] private struct DescendantBuffer { private long _element; }
}
