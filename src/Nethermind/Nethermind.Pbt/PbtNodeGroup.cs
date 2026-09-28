// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Encodes and reads a node group's canonical node payload.</summary>
/// <remarks>
/// The physical payload starts with version byte 7, followed by entries and a variable-size footer.
/// Entries are complete branch encodings in ascending post-order position order, with no padding or
/// separators; leaves are inlined in their parent branch's trailer past the whole bytes of the group
/// path (see <see cref="PbtNodeCodec"/>), so the only leaf entry is the root of a single-leaf tree. The footer contains one little-endian
/// unsigned offset of <see cref="OffsetLength"/> bytes per physically stored node, in the same order, followed by a little-endian
/// availability bitmap of one bit per position, one little-endian descendant size per set bit of the closing
/// little-endian descendant mask of one bit per boundary slot, in ascending boundary-slot order, the width byte of
/// those sizes when the mask is nonzero, and that mask. Both bitmaps take whole bytes, so with four levels per group they are
/// 32 and 16 bits. Every size takes the width, 1–6 bytes, of the
/// largest one, since the subtrees below a group are of similar size. Bit <c>n</c> of the mask is set exactly when the groups physically stored below boundary
/// slot <c>n</c> have a nonzero summed payload length, which is the size recorded for the slot. Keeping
/// the sizes per slot lets a group created between existing groups take its descendants' size from its
/// parent instead of reading them. Offsets are relative to the beginning of the entries
/// section. The first offset is zero, and subsequent offsets strictly increase; a node ends at the next
/// offset or the footer's beginning. The last position is reserved for the root and may only be present in
/// the depth-zero root group. The group key is deliberately kept outside this payload. Prefixless
/// branches strictly between the group root and its boundary are omitted when neither child is an inline leaf: only their
/// descendants are stored. Availability describes physical entries.
/// </remarks>
public static class PbtNodeGroupCodec
{
    internal const int HeaderLength = 1;
    internal static ReadOnlySpan<byte> Header => "\x07"u8;

    /// <summary>The number of positions represented by the offset table.</summary>
    public static readonly int PositionCount = PbtGroupGeometry.PositionCount;

    /// <summary>The number of boundary slots a group records descendant sizes for.</summary>
    public static readonly int DescendantSlots = PbtGroupGeometry.BoundarySlots;

    /// <summary>The number of bytes in the widest descendant-size field.</summary>
    public const int MaxDescendantBytesLength = 6;

    private const int DescendantWidthLength = 1;

    /// <summary>The number of bytes in the availability bitmap.</summary>
    internal static readonly int AvailabilityLength = (PositionCount + 7) >> 3;

    /// <summary>The number of bytes in the descendant mask that ends the footer.</summary>
    public static readonly int DescendantMaskLength = (DescendantSlots + 7) >> 3;

    /// <summary>The number of bytes in one node offset.</summary>
    /// <remarks>The entries of a group of up to six levels fit in 64 KiB; wider groups can exceed it.</remarks>
    internal static readonly int OffsetLength = PbtGroupGeometry.LevelsPerGroup <= 6 ? sizeof(ushort) : 3;

    private const int MaxNodeLength = PbtNodeCodec.MaxBranchPreimageLength + PbtNodeCodec.BranchTrailerHeaderLength + 2 * PbtStorageTreeKey.MaxLength;

    /// <summary>The largest entries section a group can hold, bounded by its offsets or, when they are wide, by its positions.</summary>
    internal static readonly int MaxEntriesLength = OffsetLength == sizeof(ushort) ? ushort.MaxValue : PositionCount * MaxNodeLength;

    /// <summary>The maximum number of bytes in the packed offset, availability and descendant-size footer.</summary>
    public static readonly int MaxTrailerLength = PositionCount * OffsetLength + AvailabilityLength + DescendantSlots * MaxDescendantBytesLength + DescendantWidthLength + DescendantMaskLength;

    /// <summary>The largest descendant size a 48-bit field can hold.</summary>
    public const long MaxDescendantBytes = (1L << (8 * MaxDescendantBytesLength)) - 1;

    /// <summary>The largest payload a group can encode: header, a full entries section, and the widest trailer.</summary>
    public static readonly int MaxPayloadLength = HeaderLength + MaxEntriesLength + MaxTrailerLength;

    /// <summary>Checks a payload's header, footer length, availability bits and descendant sizes without parsing its nodes.</summary>
    internal static void ValidateFraming(int groupDepth, ReadOnlySpan<byte> payload, out PbtBitmap availability)
    {
        if (payload.Length < HeaderLength || !payload[..HeaderLength].SequenceEqual(Header))
            throw new InvalidDataException("Unsupported or missing PBT node group format header.");
        if (payload.Length < HeaderLength + AvailabilityLength + DescendantMaskLength) throw new InvalidDataException("Truncated PBT node group footer.");
        ReadDescendantMask(payload, out PbtBitmap descendantMask);
        bool hasDescendants = !descendantMask.IsEmpty;
        if (hasDescendants)
        {
            if (payload.Length < HeaderLength + AvailabilityLength + DescendantWidthLength + DescendantMaskLength) throw new InvalidDataException("Truncated PBT node group footer.");
            if (ReadDescendantWidth(payload, hasDescendants) is < 1 or > MaxDescendantBytesLength) throw new InvalidDataException("Invalid PBT node group descendant-size width.");
        }
        if (descendantMask.AnyInRange(DescendantSlots, DescendantMaskLength * 8 - DescendantSlots)) throw new InvalidDataException("Invalid PBT node group descendant mask bits.");
        if (payload.Length < HeaderLength + AvailabilityLength + DescendantsLength(payload, descendantMask)) throw new InvalidDataException("Truncated PBT node group footer.");
        long widest = 0;
        for (int slot = descendantMask.NextSetBit(0); slot >= 0; slot = descendantMask.NextSetBit(slot + 1))
        {
            long slotBytes = ReadDescendantBytes(payload, descendantMask, slot);
            if (slotBytes == 0) throw new InvalidDataException("PBT node group descendant mask marks an empty slot.");
            widest = Math.Max(widest, slotBytes);
        }
        if (ByteWidth(widest) != ReadDescendantWidth(payload, hasDescendants)) throw new InvalidDataException("PBT node group descendant sizes are wider than the largest one.");
        payload = payload[HeaderLength..];
        ReadAvailability(payload, out availability);
        if (availability.IsEmpty || availability.AnyInRange(PositionCount, AvailabilityLength * 8 - PositionCount)
            || (groupDepth != 0 && availability.IsSet(PbtGroupGeometry.RootPosition)))
            throw new InvalidDataException("Invalid PBT node group availability bits.");
        if (payload.Length < GetTrailerLength(availability, payload)) throw new InvalidDataException("Truncated PBT node group offset table.");
    }

    /// <summary>Checks every node of a payload on top of <see cref="ValidateFraming"/>: the offset table delimits positive-length nodes and each node is structurally exact.</summary>
    internal static void ValidateNodes(scoped in PbtTraversalPath path, ReadOnlySpan<byte> payload)
    {
        ValidateFraming(path.BitDepth, payload, out PbtBitmap availability);
        payload = payload[HeaderLength..];
        int entriesLength = payload.Length - GetTrailerLength(availability, payload);
        if (entriesLength > MaxEntriesLength) throw new InvalidDataException("PBT node group entries exceed the offset limit.");
        ReadOnlySpan<byte> footer = payload[entriesLength..];

        Span<int> offsets = stackalloc int[PositionCount];
        int previousOffset = -1;
        bool foundPresent = false;
        int offsetIndex = 0;
        for (int position = availability.NextSetBit(0); position >= 0; position = availability.NextSetBit(position + 1))
        {
            int encodedOffset = ReadOffset(footer[offsetIndex..]);
            offsetIndex += OffsetLength;
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
            if (!availability.IsSet(position)) continue;
            int start = offsets[position];
            if (nextOffset <= start) throw new InvalidDataException("PBT node offsets do not delimit a positive-length node.");
            ReadOnlySpan<byte> encoding = payload[start..nextOffset];
            try
            {
                PbtNodeCodec.ValidateExact(encoding);
                if (encoding[0] == 0 && position != PbtGroupGeometry.RootPosition)
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
    internal static void ReadDescendantMask(ReadOnlySpan<byte> payload, out PbtBitmap descendantMask) =>
        PbtBitmap.Read(payload[^DescendantMaskLength..], out descendantMask);

    /// <summary>Reads the availability bitmap of a payload whose header has been stripped, without validating anything else.</summary>
    internal static void ReadAvailability(ReadOnlySpan<byte> payload, out PbtBitmap availability) =>
        PbtBitmap.Read(payload[^(AvailabilityLength + DescendantsLength(payload))..][..AvailabilityLength], out availability);

    /// <summary>Reads a node offset of <see cref="OffsetLength"/> bytes.</summary>
    internal static int ReadOffset(ReadOnlySpan<byte> offset) =>
        OffsetLength == sizeof(ushort) ? BinaryPrimitives.ReadUInt16LittleEndian(offset) : offset[0] | offset[1] << 8 | offset[2] << 16;

    private static void WriteOffset(Span<byte> destination, int offset)
    {
        if (OffsetLength == sizeof(ushort))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)offset);
            return;
        }
        destination[0] = (byte)offset;
        destination[1] = (byte)(offset >> 8);
        destination[2] = (byte)(offset >> 16);
    }

    internal static long ReadDescendantBytes(ReadOnlySpan<byte> payload, in PbtBitmap descendantMask, int slot)
    {
        int width = ReadDescendantWidth(payload, hasDescendants: true);
        int fieldsAfter = descendantMask.PopCountRange(slot + 1, DescendantSlots - slot - 1);
        ReadOnlySpan<byte> field = payload[^(DescendantWidthLength + DescendantMaskLength + (fieldsAfter + 1) * width)..][..width];
        long slotBytes = 0;
        for (int index = width - 1; index >= 0; index--) slotBytes = slotBytes << 8 | field[index];
        return slotBytes;
    }

    private static int ReadDescendantWidth(ReadOnlySpan<byte> payload, bool hasDescendants) =>
        hasDescendants ? payload[^(DescendantWidthLength + DescendantMaskLength)] : 0;

    private static int DescendantsLength(in PbtBitmap descendantMask, int width) =>
        descendantMask.IsEmpty ? DescendantMaskLength : descendantMask.PopCount() * width + DescendantWidthLength + DescendantMaskLength;

    private static int DescendantsLength(ReadOnlySpan<byte> payload, in PbtBitmap descendantMask) =>
        DescendantsLength(descendantMask, ReadDescendantWidth(payload, !descendantMask.IsEmpty));

    private static int DescendantsLength(ReadOnlySpan<byte> payload)
    {
        ReadDescendantMask(payload, out PbtBitmap descendantMask);
        return DescendantsLength(payload, descendantMask);
    }

    /// <summary>The width every descendant-size field takes: the byte count of the largest masked size, or zero for none.</summary>
    private static int DescendantWidth(ReadOnlySpan<long> descendantBytes, in PbtBitmap descendantMask)
    {
        long widest = 0;
        for (int slot = descendantMask.NextSetBit(0); slot >= 0; slot = descendantMask.NextSetBit(slot + 1))
            widest = Math.Max(widest, descendantBytes[slot]);
        return ByteWidth(widest);
    }

    private static int ByteWidth(long value) => (64 - BitOperations.LeadingZeroCount((ulong)value) + 7) / 8;

    private static int OffsetsAndAvailabilityLength(in PbtBitmap availability) => availability.PopCount() * OffsetLength + AvailabilityLength;

    /// <summary>The footer length of a group being written.</summary>
    /// <param name="descendantMask">The <see cref="DescendantMask"/> of <paramref name="descendantBytes"/>.</param>
    internal static int GetTrailerLength(in PbtBitmap availability, in PbtBitmap descendantMask, ReadOnlySpan<long> descendantBytes) =>
        OffsetsAndAvailabilityLength(availability) + DescendantsLength(descendantMask, DescendantWidth(descendantBytes, descendantMask));

    /// <summary>The footer length of a stored payload, with or without its header.</summary>
    internal static int GetTrailerLength(in PbtBitmap availability, ReadOnlySpan<byte> payload) =>
        OffsetsAndAvailabilityLength(availability) + DescendantsLength(payload);

    /// <summary>Validates per-slot descendant sizes and sets the mask of nonzero slots; an empty span means no descendants.</summary>
    /// <param name="candidateSlots">The slots that may be nonzero; every other slot is known to be zero and is not read.</param>
    internal static void DescendantMask(ReadOnlySpan<long> descendantBytes, in PbtBitmap candidateSlots, out PbtBitmap descendantMask)
    {
        descendantMask = default;
        if (descendantBytes.IsEmpty) return;
        if (descendantBytes.Length != DescendantSlots) throw new ArgumentException("One size per boundary slot is required.", nameof(descendantBytes));
        for (int slot = candidateSlots.NextSetBit(0); slot >= 0; slot = candidateSlots.NextSetBit(slot + 1))
        {
            long slotBytes = descendantBytes[slot];
            ArgumentOutOfRangeException.ThrowIfNegative(slotBytes, nameof(descendantBytes));
            if (slotBytes > MaxDescendantBytes) throw new InvalidDataException("PBT node group descendant size exceeds the uint48 limit.");
            if (slotBytes != 0) descendantMask.Set(slot);
        }
    }

    /// <param name="descendantMask">The <see cref="DescendantMask"/> of <paramref name="descendantBytes"/>.</param>
    internal static void WriteFooter(Span<byte> footer, ReadOnlySpan<int> offsets, in PbtBitmap availability, in PbtBitmap descendantMask, ReadOnlySpan<long> descendantBytes)
    {
        int offsetIndex = 0;
        for (int position = availability.NextSetBit(0); position >= 0; position = availability.NextSetBit(position + 1))
        {
            WriteOffset(footer[offsetIndex..], offsets[position]);
            offsetIndex += OffsetLength;
        }
        availability.Write(footer.Slice(offsetIndex, AvailabilityLength));
        Span<byte> field = footer[(offsetIndex + AvailabilityLength)..];
        int width = DescendantWidth(descendantBytes, descendantMask);
        for (int slot = descendantMask.NextSetBit(0); slot >= 0; slot = descendantMask.NextSetBit(slot + 1))
        {
            long slotBytes = descendantBytes[slot];
            for (int index = 0; index < width; index++) field[index] = (byte)(slotBytes >> (8 * index));
            field = field[width..];
        }
        if (!descendantMask.IsEmpty)
        {
            field[0] = (byte)width;
            field = field[DescendantWidthLength..];
        }
        descendantMask.Write(field[..DescendantMaskLength]);
    }

    private static readonly int PrefixlessBranchLength = PbtNodeCodec.BranchLength(0, 0, 0);

    /// <summary>A prefixless interior branch without inline leaves is reconstructed from its children, so it need not be stored.</summary>
    /// <remarks>Interior positions are the ones strictly narrower than the group root and wider than a boundary slot in <see cref="PbtGroupGeometry.WidthOf"/>.</remarks>
    internal static bool ShouldOmit(int position, ReadOnlySpan<byte> encoding) =>
        PbtGroupGeometry.WidthOf(position) is var width && width > 1 && width < PbtGroupGeometry.BoundarySlots
        && encoding.Length == PrefixlessBranchLength && encoding[0] == 1 && encoding[1] == 0 && encoding[2] == 0
        && encoding[PrefixlessBranchLength - 2] == 0 && encoding[PrefixlessBranchLength - 1] == 0;
}

/// <summary>Provides a validated, allocation-free view over a borrowed node-group payload.</summary>
public readonly ref struct PbtNodeGroupReader
{
    private readonly int _groupDepth;
    private readonly ReadOnlySpan<byte> _payload;
    private readonly int _entriesLength;
    private readonly PbtBitmap _availability;
    private readonly PbtBitmap _descendantMask;
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
        Debug.Assert(PbtGroupGeometry.IsGroupDepth(groupDepth));
        payload = payload[PbtNodeGroupCodec.HeaderLength..];
        PbtNodeGroupCodec.ReadDescendantMask(payload, out _descendantMask);
        PbtNodeGroupCodec.ReadAvailability(payload, out _availability);

        _groupDepth = groupDepth;
        _payload = payload;
        _entriesLength = payload.Length - PbtNodeGroupCodec.GetTrailerLength(_availability, payload);
        _initialized = true;
    }

    /// <summary>Gets the summed payload lengths of the groups physically stored below boundary slot <paramref name="slot"/>.</summary>
    public long DescendantBytes(int slot)
    {
        EnsureInitialized();
        if ((uint)slot >= PbtNodeGroupCodec.DescendantSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        return !_descendantMask.IsSet(slot) ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(_payload, _descendantMask, slot);
    }
    /// <summary>Gets the number of nodes in this group.</summary>
    public int Count { get { EnsureInitialized(); return _availability.PopCount(); } }
    /// <summary>Gets a borrowed canonical node encoding at a position.</summary>
    public ReadOnlySpan<byte> GetNode(int position)
    {
        ValidatePosition(position);
        return !_availability.IsSet(position) ? [] : NodeAt(position);
    }
    /// <summary>Attempts to get a borrowed canonical node encoding.</summary>
    public bool TryGetNode(int position, out ReadOnlySpan<byte> encoding)
    {
        ValidatePosition(position);
        if (!_availability.IsSet(position)) { encoding = default; return false; }
        encoding = NodeAt(position);
        return true;
    }

    private ReadOnlySpan<byte> NodeAt(int position)
    {
        int rank = _availability.PopCountBelow(position);
        ReadOnlySpan<byte> offsets = _payload[(_entriesLength + rank * PbtNodeGroupCodec.OffsetLength)..];
        int start = PbtNodeGroupCodec.ReadOffset(offsets);
        int end = rank + 1 == _availability.PopCount() ? _entriesLength : PbtNodeGroupCodec.ReadOffset(offsets[PbtNodeGroupCodec.OffsetLength..]);
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
        if ((uint)position >= PbtNodeGroupCodec.PositionCount || (position == PbtGroupGeometry.RootPosition && _groupDepth != 0))
            throw new ArgumentOutOfRangeException(nameof(position));
    }
    private void EnsureInitialized() { if (!_initialized) throw new InvalidOperationException("The PBT node-group reader is not initialized."); }
    /// <summary>Checks that a branch's inline leaf keys lie below the branch's position.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void ValidateLeafPath(scoped in PbtTraversalPath groupKey, int position, ReadOnlySpan<byte> encoding)
    {
        if (encoding[0] == 0)
        {
            if (position != PbtGroupGeometry.RootPosition) throw new InvalidDataException("A PBT leaf entry is only valid as the tree root.");
            return;
        }
        if (position == PbtGroupGeometry.RootPosition) return;
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
        Span<byte> directions = stackalloc byte[PbtGroupGeometry.LevelsPerGroup];
        int relativeDepth = RelativeDirections(position, directions);
        ValidateInlineLeafPath(groupKey, node.LeftKeyPostfix, directions[..relativeDepth]);
        ValidateInlineLeafPath(groupKey, node.RightKeyPostfix, directions[..relativeDepth]);
    }

    private static void ValidateInlineLeafPath(scoped in PbtTraversalPath groupKey, ReadOnlySpan<byte> keyPostfix, ReadOnlySpan<byte> directions)
    {
        if (keyPostfix.IsEmpty) return;
        int relativeDepth = directions.Length;
        // The leaf hangs at least one level below the branch.
        int completeBytes = groupKey.BitDepth >> 3;
        int requiredDepth = checked(groupKey.BitDepth + relativeDepth + 1);
        if ((completeBytes + keyPostfix.Length) * 8 < requiredDepth) throw new InvalidDataException("PBT leaf does not match its group position.");
        if (completeBytes + keyPostfix.Length > PbtStorageTreeKey.MaxLength) throw new InvalidDataException("An inline PBT leaf key exceeds the maximum key length.");

        // The group tail and relative path span at most the postfix's first two bytes, which the depth check ensures exist.
        int groupTailBits = groupKey.BitDepth & 7;
        int expectedTail = groupTailBits == 0 ? 0 : groupKey.Bytes[completeBytes] << 8;
        for (int index = 0; index < relativeDepth; index++)
            expectedTail |= directions[index] << (15 - groupTailBits - index);
        int tailMask = (0xFFFF << (16 - groupTailBits - relativeDepth)) & 0xFFFF;
        int actualTail = keyPostfix[0] << 8 | (keyPostfix.Length > 1 ? keyPostfix[1] : 0);
        if (((actualTail ^ expectedTail) & tailMask) != 0)
            throw new InvalidDataException("PBT leaf does not match its group position.");
    }
    private static int RelativeDirections(int position, Span<byte> directions)
    {
        int currentPosition = PbtGroupGeometry.RootPosition, width = PbtGroupGeometry.BoundarySlots, depth = 0;
        while (depth < PbtGroupGeometry.LevelsPerGroup && position != currentPosition)
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
                if (!_reader._availability.IsSet(_position)) continue;
                CurrentPosition = _position; Current = _reader.GetNode(_position); return true;
            }
            Current = default; CurrentPosition = -1; return false;
        }
    }
}
