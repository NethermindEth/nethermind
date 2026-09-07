// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>Encodes and reads a four-level node group's canonical node payload.</summary>
/// <remarks>
/// The physical payload consists of an entries section followed by a fixed-size footer. Entries are
/// complete, self-delimiting node encodings in ascending post-order position order, with no padding
/// or separators. The footer contains 31 little-endian unsigned 16-bit offsets, one for each
/// position, relative to the beginning of the entries section, followed by a little-endian unsigned
/// 32-bit availability bitmap. An absent position has offset zero. Consequently the first present
/// node starts at offset zero, and every subsequent present offset is strictly greater than the
/// preceding one; a node ends at the next present offset or at the beginning of the footer.
/// Position 30 is reserved for the root and may only be present in the depth-zero root group. The
/// group key is deliberately kept outside this payload.
/// </remarks>
public static class PbtNodeGroupCodec
{
    /// <summary>The number of positions represented by the offset table.</summary>
    public const int PositionCount = PbtFourLevelGroupGeometry.PositionCount;

    /// <summary>The number of bytes in the fixed offset-and-availability footer.</summary>
    public const int TrailerLength = PositionCount * sizeof(ushort) + sizeof(uint);

    private const uint ReservedRootBit = 1u << PbtFourLevelGroupGeometry.RootPosition;
    private const uint AllowedPositionBits = (1u << PbtFourLevelGroupGeometry.PositionCount) - 1;
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
    public static void Encode(ref BufferWriter writer, PbtNodePath groupKey, IReadOnlyList<PbtNodeRecord> nodes)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ArgumentNullException.ThrowIfNull(nodes);
        ValidateGroupKey(groupKey);
        if (nodes.Count == 0) throw new InvalidDataException("A PBT node group cannot be empty.");
        if (nodes.Count > PositionCount) throw new InvalidDataException("A PBT node group has too many nodes.");

        Span<int> recordIndices = stackalloc int[PositionCount];
        recordIndices.Fill(-1);
        uint availability = 0;
        int entriesLength = 0;
        for (int index = 0; index < nodes.Count; index++)
        {
            PbtNodeRecord record = nodes[index] ?? throw new InvalidDataException("A PBT node group contains a null record.");
            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(record.Path);
            if (!location.GroupKey.Equals(groupKey)) throw new InvalidDataException("Node does not belong to the group key.");
            if ((uint)location.Position >= PositionCount
                || (location.Position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0))
                throw new InvalidDataException("The group contains a reserved node position.");

            uint bit = 1u << location.Position;
            if ((availability & bit) != 0) throw new InvalidDataException("Duplicate node position in group.");
            ReadOnlySpan<byte> encoding = record.Encoding.Span;
            PbtNode node = PbtNodeCodec.Decode(encoding);
            ValidateNodePath(node, record.Path);
            entriesLength = checked(entriesLength + encoding.Length);
            if (entriesLength > MaxOffset) throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
            recordIndices[location.Position] = index;
            availability |= bit;
        }

        int initialWrittenCount = writer.WrittenCount;
        try
        {
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

            Span<byte> footer = stackalloc byte[TrailerLength];
            for (int position = 0; position < PositionCount; position++)
                BinaryPrimitives.WriteUInt16LittleEndian(footer[(position * sizeof(ushort))..], offsets[position]);
            BinaryPrimitives.WriteUInt32LittleEndian(footer[(PositionCount * sizeof(ushort))..], availability);
            footer.CopyTo(writer.GetSpan(TrailerLength));
            writer.Advance(TrailerLength);
        }
        catch
        {
            writer.Reset(initialWrittenCount);
            throw;
        }
    }

    internal static void Encode(ref BufferWriter writer, PbtNodePath groupKey, ReadOnlyMemory<byte>[] encodings, bool[] present)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ArgumentNullException.ThrowIfNull(encodings);
        ArgumentNullException.ThrowIfNull(present);
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
            PbtNode node = PbtNodeCodec.Decode(encoding);
            ValidateNodePath(node, PbtFourLevelGroupGeometry.PathOf(groupKey, position));
            entriesLength = checked(entriesLength + encoding.Length);
            if (entriesLength > MaxOffset) throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
            availability |= 1u << position;
        }

        if (availability == 0) throw new InvalidDataException("A PBT node group cannot be empty.");
        int initialWrittenCount = writer.WrittenCount;
        try
        {
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

            Span<byte> footer = stackalloc byte[TrailerLength];
            for (int position = 0; position < PositionCount; position++)
                BinaryPrimitives.WriteUInt16LittleEndian(footer[(position * sizeof(ushort))..], offsets[position]);
            BinaryPrimitives.WriteUInt32LittleEndian(footer[(PositionCount * sizeof(ushort))..], availability);
            footer.CopyTo(writer.GetSpan(TrailerLength));
            writer.Advance(TrailerLength);
        }
        catch
        {
            writer.Reset(initialWrittenCount);
            throw;
        }
    }

    internal static void ValidateNodeEncoding(PbtNodePath path, ReadOnlySpan<byte> encoding)
    {
        if (encoding.IsEmpty) throw new InvalidDataException("A PBT snapshot node encoding cannot be empty.");
        PbtNodeCodec.ValidateExact(encoding);
        ValidateNodePath(PbtNodeCodec.Decode(encoding), path);
    }

    private static void ValidateNodePath(PbtNode node, PbtNodePath path)
    {
        if (node is PbtLeafNode leaf && !PbtNodePath.FromKey(leaf.Key, path.BitDepth).Equals(path))
            throw new InvalidDataException("PBT leaf does not match its group position.");
    }

    private static void ValidateGroupKey(PbtNodePath groupKey)
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
    private readonly PbtNodePath? _groupKey;
    private readonly ReadOnlySpan<byte> _payload;
    private readonly OffsetBuffer _offsets;
    private readonly LengthBuffer _lengths;
    private readonly uint _availability;
    private readonly bool _initialized;

    /// <summary>Validates and borrows a complete node-group payload.</summary>
    public PbtNodeGroupReader(PbtNodePath groupKey, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ValidateGroupKey(groupKey);
        if (payload.Length < PbtNodeGroupCodec.TrailerLength) throw new InvalidDataException("Truncated PBT node group footer.");
        int entriesLength = payload.Length - PbtNodeGroupCodec.TrailerLength;
        ReadOnlySpan<byte> footer = payload[entriesLength..];
        uint availability = BinaryPrimitives.ReadUInt32LittleEndian(footer[(PbtNodeGroupCodec.PositionCount * sizeof(ushort))..]);
        if (availability == 0 || (availability & ~AllowedPositionBits) != 0 || (groupKey.BitDepth != 0 && (availability & ReservedRootBit) != 0))
            throw new InvalidDataException("Invalid PBT node group availability bits.");

        OffsetBuffer offsets = default;
        LengthBuffer lengths = default;
        int previousOffset = -1;
        bool foundPresent = false;
        for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
        {
            ushort encodedOffset = BinaryPrimitives.ReadUInt16LittleEndian(footer[(position * sizeof(ushort))..]);
            bool present = (availability & (1u << position)) != 0;
            if (!present)
            {
                if (encodedOffset != 0) throw new InvalidDataException("Absent PBT node positions must have zero offsets.");
                continue;
            }
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
    public PbtNodePath GroupKey => InitializedGroupKey();
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
        offset = _offsets[position];
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

    private PbtNodePath InitializedGroupKey() { EnsureInitialized(); return _groupKey!; }
    private void ValidatePosition(int position)
    {
        EnsureInitialized();
        if ((uint)position >= PbtNodeGroupCodec.PositionCount || (position == PbtFourLevelGroupGeometry.RootPosition && _groupKey!.BitDepth != 0))
            throw new ArgumentOutOfRangeException(nameof(position));
    }
    private void EnsureInitialized() { if (!_initialized) throw new InvalidOperationException("The PBT node-group reader is not initialized."); }
    private static void ValidateGroupKey(PbtNodePath groupKey)
    {
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth)) throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
    }
    private static void ValidateLeafPath(PbtNodePath groupKey, int position, ReadOnlySpan<byte> encoding)
    {
        if (encoding[0] != 0 || position == PbtFourLevelGroupGeometry.RootPosition) return;
        Span<byte> directions = stackalloc byte[PbtFourLevelGroupGeometry.LevelsPerGroup];
        int relativeDepth = RelativeDirections(position, directions);
        int requiredDepth = checked(groupKey.BitDepth + relativeDepth);
        int keyLength = BinaryPrimitives.ReadUInt16BigEndian(encoding[1..]);
        if (keyLength * 8 < requiredDepth) throw new InvalidDataException("PBT leaf does not match its group position.");
        ReadOnlySpan<byte> key = encoding.Slice(3, keyLength);
        int completeBytes = groupKey.BitDepth >> 3;
        if (!key[..completeBytes].SequenceEqual(groupKey.Path[..completeBytes]))
            throw new InvalidDataException("PBT leaf does not match its group position.");

        // Four-level group alignment keeps the group tail and relative path in one byte.
        int groupTailBits = groupKey.BitDepth & 7;
        int expectedTail = groupTailBits == 0 ? 0 : groupKey.Path[completeBytes];
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
