// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>Encodes and reads a four-level node group's canonical node payload.</summary>
/// <remarks>
/// The physical payload starts with version byte 2, followed by entries and a variable-size footer.
/// Entries are complete node encodings in ascending post-order position order, with no padding or
/// separators. The footer contains one little-endian unsigned 16-bit offset per physically stored
/// node, in the same order, followed by a little-endian unsigned 32-bit availability bitmap.
/// Offsets are relative to the beginning of the entries section. The first offset is zero, and
/// subsequent offsets strictly increase; a node ends at the next offset or the footer's beginning.
/// Position 30 is reserved for the root and may only be present in the depth-zero root group. The
/// group key is deliberately kept outside this payload. Prefixless branches at relative depths 1–3
/// are implicit: only their descendants are stored. Availability describes physical entries.
/// </remarks>
public static class PbtNodeGroupCodec
{
    internal const int HeaderLength = 1;
    internal static ReadOnlySpan<byte> Header => "\x02"u8;

    /// <summary>The number of positions represented by the offset table.</summary>
    public const int PositionCount = PbtFourLevelGroupGeometry.PositionCount;

    /// <summary>The maximum number of bytes in the packed offset-and-availability footer.</summary>
    public const int MaxTrailerLength = PositionCount * sizeof(ushort) + sizeof(uint);

    private const int MaxOffset = ushort.MaxValue;

    /// <summary>
    /// Validates and writes a canonical group payload directly through <paramref name="writer"/>.
    /// </summary>
    /// <remarks>
    /// Validation completes before the first write. Node encodings are then copied once in position
    /// order, followed by the offset table and availability bitmap. If writing fails, the writer's
    /// <see cref="BufferWriter.WrittenCount"/> is restored to its value on entry; bytes in a caller
    /// supplied destination beyond that count are not part of the output.
    /// </remarks>
    /// <param name="writer">The destination writer, passed by reference because it is mutable.</param>
    /// <param name="groupKey">The four-level key identifying the group.</param>
    /// <param name="nodes">Nodes belonging to this group, each with its complete canonical path and encoding.</param>
    public static void Encode<TPath>(ref BufferWriter writer, TPath groupKey, IReadOnlyList<PbtNodeRecord> nodes)
        where TPath : struct, IPbtNodePath<TPath>
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ValidateGroupKey(groupKey);
        if (nodes.Count == 0) throw new InvalidDataException("A PBT node group cannot be empty.");
        if (nodes.Count > PositionCount) throw new InvalidDataException("A PBT node group has too many nodes.");

        Span<int> recordIndices = stackalloc int[PositionCount];
        recordIndices.Fill(-1);
        uint availability = 0;
        uint seenPositions = 0;
        int entriesLength = 0;
        for (int index = 0; index < nodes.Count; index++)
        {
            PbtNodeRecord record = nodes[index] ?? throw new InvalidDataException("A PBT node group contains a null record.");
            PbtNodeGroupLocation<PbtStorageNodePath> location = PbtFourLevelGroupGeometry.Locate(record.Path);
            if (!location.GroupKey.Equals(groupKey)) throw new InvalidDataException("Node does not belong to the group key.");
            if ((uint)location.Position >= PositionCount
                || (location.Position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0))
                throw new InvalidDataException("The group contains a reserved node position.");

            uint bit = 1u << location.Position;
            if ((seenPositions & bit) != 0) throw new InvalidDataException("Duplicate node position in group.");
            seenPositions |= bit;
            ReadOnlySpan<byte> encoding = record.Encoding.Span;
            PbtNodeReader node = new(encoding);
            ValidateNodePath(node, record.Path);
            if (ShouldOmit(location.Position, encoding)) continue;
            entriesLength = checked(entriesLength + encoding.Length);
            if (entriesLength > MaxOffset) throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
            recordIndices[location.Position] = index;
            availability |= bit;
        }

        if (availability == 0) throw new InvalidDataException("A PBT node group cannot be empty.");
        int initialWrittenCount = writer.WrittenCount;
        try
        {
            writer.Write(Header);
            Span<ushort> offsets = stackalloc ushort[PositionCount];
            int offset = 0;
            for (int position = 0; position < PositionCount; position++)
            {
                int recordIndex = recordIndices[position];
                if (recordIndex < 0) continue;
                if ((uint)offset > MaxOffset) throw new InvalidDataException("PBT node group start offset exceeds the uint16 limit.");
                offsets[position] = (ushort)offset;
                ReadOnlySpan<byte> encoding = nodes[recordIndex].Encoding.Span;
                writer.Write(encoding);
                offset = checked(offset + encoding.Length);
            }

            int trailerLength = GetTrailerLength(availability);
            WriteFooter(writer.GetSpan(trailerLength), offsets, availability);
            writer.Advance(trailerLength);
        }
        catch
        {
            writer.Reset(initialWrittenCount);
            throw;
        }
    }

    internal static void Encode<TPath>(ref BufferWriter writer, TPath groupKey, scoped ReadOnlySpan<ReadOnlyMemory<byte>> encodings, scoped ReadOnlySpan<bool> present)
        where TPath : struct, IPbtNodePath<TPath>
    {
        ValidateGroupKey(groupKey);
        if (encodings.Length != PositionCount || present.Length != PositionCount)
            throw new ArgumentException("A PBT node group must have one slot per position.");

        uint availability = 0;
        int entriesLength = 0;
        for (int position = 0; position < PositionCount; position++)
        {
            if (!present[position]) continue;
            ReadOnlySpan<byte> encoding = encodings[position].Span;
            if (encoding.IsEmpty) continue;
            if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0)
                throw new InvalidDataException("The group contains a reserved node position.");
            PbtNodeReader node = new(encoding);
            ValidateNodePath(node, PbtFourLevelGroupGeometry.PathOf(groupKey, position));
            if (ShouldOmit(position, encoding)) continue;
            entriesLength = checked(entriesLength + encoding.Length);
            if (entriesLength > MaxOffset) throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
            availability |= 1u << position;
        }

        if (availability == 0) throw new InvalidDataException("A PBT node group cannot be empty.");
        int initialWrittenCount = writer.WrittenCount;
        try
        {
            writer.Write(Header);
            Span<ushort> offsets = stackalloc ushort[PositionCount];
            int offset = 0;
            for (int position = 0; position < PositionCount; position++)
            {
                if ((availability & (1u << position)) == 0) continue;
                offsets[position] = (ushort)offset;
                ReadOnlySpan<byte> encoding = encodings[position].Span;
                writer.Write(encoding);
                offset = checked(offset + encoding.Length);
            }

            int trailerLength = GetTrailerLength(availability);
            WriteFooter(writer.GetSpan(trailerLength), offsets, availability);
            writer.Advance(trailerLength);
        }
        catch
        {
            writer.Reset(initialWrittenCount);
            throw;
        }
    }

    internal static int GetTrailerLength(uint availability) => BitOperations.PopCount(availability) * sizeof(ushort) + sizeof(uint);

    internal static void WriteFooter(Span<byte> footer, ReadOnlySpan<ushort> offsets, uint availability)
    {
        int offsetIndex = 0;
        for (int position = 0; position < PositionCount; position++)
        {
            if ((availability & (1u << position)) == 0) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(footer[offsetIndex..], offsets[position]);
            offsetIndex += sizeof(ushort);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(footer[offsetIndex..], availability);
    }

    internal static bool ShouldOmit(int position, ReadOnlySpan<byte> encoding) =>
        PbtFourLevelGroupGeometry.WidthOf(position) is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots
        && encoding[0] == 1 && encoding[1] == 0 && encoding[2] == 0;

    internal static void ValidateNodeEncoding<TPath>(TPath path, ReadOnlySpan<byte> encoding) where TPath : struct, IPbtNodePath<TPath>
    {
        if (encoding.IsEmpty) throw new InvalidDataException("A PBT snapshot node encoding cannot be empty.");
        ValidateNodePath(new PbtNodeReader(encoding), path);
    }

    private static void ValidateNodePath<TPath>(PbtNodeReader node, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!node.IsLeaf) return;
        if (!path.MatchesPrefix(node.Key, path.BitDepth))
            throw new InvalidDataException("PBT leaf does not match its group position.");
    }

    private static void ValidateGroupKey<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
    }
}

/// <summary>Provides a validated, allocation-free view over a borrowed node-group payload.</summary>
public readonly ref struct PbtNodeGroupReader
{
    private const uint ReservedRootBit = 1u << PbtFourLevelGroupGeometry.RootPosition;
    private const uint AllowedPositionBits = (1u << PbtFourLevelGroupGeometry.PositionCount) - 1;
    private readonly PbtStorageNodePath _groupKey;
    private readonly ReadOnlySpan<byte> _payload;
    private readonly OffsetBuffer _offsets;
    private readonly LengthBuffer _lengths;
    private readonly uint _availability;
    private readonly bool _initialized;

    /// <summary>Validates and borrows a complete node-group payload.</summary>
    /// <remarks>The group identity is snapshotted; advancing the cursor cannot change this reader or its enumerators.
    /// The payload must remain valid and immutable for the lifetime of the reader and its enumerators.</remarks>
    public PbtNodeGroupReader(scoped PbtTraversalPath path, ReadOnlySpan<byte> payload)
    {
        PbtStorageNodePath groupKey = path.ToPath<PbtStorageNodePath>();
        ValidateGroupKey(groupKey);
        if (payload.Length < PbtNodeGroupCodec.HeaderLength || !payload[..PbtNodeGroupCodec.HeaderLength].SequenceEqual(PbtNodeGroupCodec.Header))
            throw new InvalidDataException("Unsupported or missing PBT node group format header.");
        payload = payload[PbtNodeGroupCodec.HeaderLength..];
        if (payload.Length < sizeof(uint)) throw new InvalidDataException("Truncated PBT node group footer.");
        uint availability = BinaryPrimitives.ReadUInt32LittleEndian(payload[^sizeof(uint)..]);
        if (availability == 0 || (availability & ~AllowedPositionBits) != 0 || (groupKey.BitDepth != 0 && (availability & ReservedRootBit) != 0))
            throw new InvalidDataException("Invalid PBT node group availability bits.");
        int trailerLength = PbtNodeGroupCodec.GetTrailerLength(availability);
        if (payload.Length < trailerLength) throw new InvalidDataException("Truncated PBT node group offset table.");
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
                ValidateLeafPath(groupKey, position, encoding);
            }
            catch (InvalidDataException exception) { throw new InvalidDataException("Invalid PBT node in group.", exception); }
            lengths[position] = nextOffset - start;
            nextOffset = start;
        }

        _groupKey = groupKey;
        _payload = payload;
        _offsets = offsets;
        _lengths = lengths;
        _availability = availability;
        _initialized = true;
    }

    /// <summary>Gets the key identifying this group.</summary>
    public PbtStorageNodePath GroupKey => InitializedGroupKey();
    /// <summary>Gets the availability bits.</summary>
    public uint Availability { get { EnsureInitialized(); return _availability; } }
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

    private PbtStorageNodePath InitializedGroupKey() { EnsureInitialized(); return _groupKey; }
    private void ValidatePosition(int position)
    {
        EnsureInitialized();
        if ((uint)position >= PbtNodeGroupCodec.PositionCount || (position == PbtFourLevelGroupGeometry.RootPosition && _groupKey.BitDepth != 0))
            throw new ArgumentOutOfRangeException(nameof(position));
    }
    private void EnsureInitialized() { if (!_initialized) throw new InvalidOperationException("The PBT node-group reader is not initialized."); }
    private static void ValidateGroupKey(PbtStorageNodePath groupKey)
    {
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth)) throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
    }
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void ValidateLeafPath<TPath>(TPath groupKey, int position, ReadOnlySpan<byte> encoding) where TPath : struct, IPbtNodePath<TPath>
    {
        if (encoding[0] != 0 || position == PbtFourLevelGroupGeometry.RootPosition) return;
        Span<byte> directions = stackalloc byte[PbtFourLevelGroupGeometry.LevelsPerGroup];
        int relativeDepth = RelativeDirections(position, directions);
        int requiredDepth = checked(groupKey.BitDepth + relativeDepth);
        int keyLength = BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]);
        if (keyLength * 8 < requiredDepth) throw new InvalidDataException("PBT leaf does not match its group position.");
        ReadOnlySpan<byte> key = encoding.Slice(3, keyLength);
        int completeBytes = groupKey.BitDepth >> 3;
        if (!groupKey.MatchesPrefix(key, completeBytes * 8))
            throw new InvalidDataException("PBT leaf does not match its group position.");

        // Four-level group alignment keeps the group tail and relative path in one byte.
        int groupTailBits = groupKey.BitDepth & 7;
        int expectedTail = groupTailBits == 0 ? 0 : groupKey.GetByte(completeBytes);
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
}
