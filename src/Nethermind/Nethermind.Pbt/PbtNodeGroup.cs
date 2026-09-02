// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Encodes and reads a four-level node group's canonical node payload.</summary>
/// <remarks>
/// Nodes are concatenated in post-order position order and terminated by a four-byte little-endian
/// presence word. The root position is reserved for the root group and is never encoded in ordinary
/// groups. The group key is deliberately kept outside this payload.
/// </remarks>
public static class PbtNodeGroupCodec
{
    /// <summary>The number of bytes in the availability trailer.</summary>
    public const int TrailerLength = sizeof(uint);

    private const uint ReservedRootBit = 1u << PbtFourLevelGroupGeometry.RootPosition;
    private const uint AllowedPositionBits = (1u << PbtFourLevelGroupGeometry.PositionCount) - 1;

    /// <summary>Encodes the supplied nodes in ascending topological position order.</summary>
    /// <param name="groupKey">The four-level key identifying the group.</param>
    /// <param name="nodes">Nodes belonging to this group, each with its complete canonical path and encoding.</param>
    /// <returns>The canonical group payload.</returns>
    public static byte[] Encode(PbtNodePath groupKey, IReadOnlyList<PbtNodeRecord> nodes)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ArgumentNullException.ThrowIfNull(nodes);
        ValidateGroupKey(groupKey);

        uint availability = 0;
        int payloadLength = TrailerLength;
        int[] positions = new int[nodes.Count];
        byte[][] encodings = new byte[nodes.Count][];
        for (int index = 0; index < nodes.Count; index++)
        {
            PbtNodeRecord record = nodes[index];
            ArgumentNullException.ThrowIfNull(record);
            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(record.Path);
            if (!location.GroupKey.Equals(groupKey)) throw new InvalidDataException("Node does not belong to the group key.");
            if ((uint)location.Position >= PbtFourLevelGroupGeometry.PositionCount
                || (location.Position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0))
                throw new InvalidDataException("The group contains a reserved node position.");
            uint bit = 1u << location.Position;
            if ((availability & bit) != 0) throw new InvalidDataException("Duplicate node position in group.");
            byte[] encoding = record.Encoding.ToArray();
            PbtNode node = PbtNodeCodec.Decode(encoding);
            ValidateNodePath(node, record.Path);
            positions[index] = location.Position;
            encodings[index] = encoding;
            availability |= bit;
            payloadLength = checked(payloadLength + encoding.Length);
        }

        Array.Sort(positions, encodings);
        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        int offset = 0;
        for (int index = 0; index < encodings.Length; index++)
        {
            encodings[index].AsSpan().CopyTo(payload.AsSpan(offset));
            offset += encodings[index].Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), availability);
        return payload;
    }

    /// <summary>Decodes and validates a complete group payload.</summary>
    /// <param name="groupKey">The four-level key identifying the group.</param>
    /// <param name="payload">The complete payload, including its four-byte trailer.</param>
    /// <returns>A read-only group view that owns a copy of the payload.</returns>
    public static PbtNodeGroup Decode(PbtNodePath groupKey, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ValidateGroupKey(groupKey);
        if (payload.Length < TrailerLength) throw new InvalidDataException("Truncated PBT node group trailer.");

        uint availability = BinaryPrimitives.ReadUInt32LittleEndian(payload[^TrailerLength..]);
        if (availability == 0 || (availability & ~AllowedPositionBits) != 0 || (groupKey.BitDepth != 0 && (availability & ReservedRootBit) != 0))
            throw new InvalidDataException("Invalid PBT node group availability bits.");

        byte[] ownedPayload = payload.ToArray();
        int[] offsets = new int[PbtFourLevelGroupGeometry.PositionCount];
        int[] lengths = new int[PbtFourLevelGroupGeometry.PositionCount];
        int offset = 0;
        int entriesLength = payload.Length - TrailerLength;
        for (int position = 0; position < PbtFourLevelGroupGeometry.PositionCount; position++)
        {
            if ((availability & (1u << position)) == 0) continue;
            if (offset >= entriesLength) throw new InvalidDataException("PBT node group node data is truncated.");
            PbtNode node;
            int consumed;
            try
            {
                node = PbtNodeCodec.Decode(payload.Slice(offset, entriesLength - offset), out consumed);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Invalid PBT node in group.", exception);
            }
            PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            try
            {
                ValidateNodePath(node, path);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("PBT node path does not match its group position.", exception);
            }
            offsets[position] = offset;
            lengths[position] = consumed;
            offset = checked(offset + consumed);
        }
        if (offset != payload.Length - TrailerLength) throw new InvalidDataException("PBT node group payload length does not match its availability bits.");
        return new PbtNodeGroup(groupKey, ownedPayload, availability, offsets, lengths);
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

/// <summary>A validated, read-only view of a node-group payload.</summary>
public sealed class PbtNodeGroup
{
    private readonly byte[] _payload;
    private readonly int[] _offsets;
    private readonly int[] _lengths;

    internal PbtNodeGroup(PbtNodePath groupKey, byte[] payload, uint availability, int[] offsets, int[] lengths)
    {
        GroupKey = groupKey;
        _payload = payload;
        Availability = availability;
        _offsets = offsets;
        _lengths = lengths;
    }

    /// <summary>Gets the key identifying this group.</summary>
    public PbtNodePath GroupKey { get; }

    /// <summary>Gets the availability bits, with bit 0 representing position zero.</summary>
    public uint Availability { get; }

    /// <summary>Gets the number of nodes in this group.</summary>
    public int Count => BitOperations.PopCount(Availability);

    /// <summary>Gets the complete canonical node encoding at a position, or <see langword="null"/> when absent.</summary>
    public ReadOnlyMemory<byte>? GetNode(int position)
    {
        ValidatePosition(position);
        if ((Availability & (1u << position)) == 0) return null;
        return _payload.AsMemory(_offsets[position], _lengths[position]);
    }

    /// <summary>Gets the complete canonical node encoding at a position, or <see langword="null"/> when absent.</summary>
    public ReadOnlyMemory<byte>? this[int position] => GetNode(position);

    /// <summary>Attempts to get the complete canonical node encoding at a position.</summary>
    public bool TryGetNode(int position, out ReadOnlyMemory<byte> encoding)
    {
        ReadOnlyMemory<byte>? result = GetNode(position);
        if (result.HasValue)
        {
            encoding = result.Value;
            return true;
        }

        encoding = default;
        return false;
    }

    /// <summary>Enumerates present nodes in ascending topological-position order.</summary>
    public IEnumerable<PbtNodeRecord> EnumerateNodes()
    {
        for (int position = 0; position < PbtFourLevelGroupGeometry.PositionCount; position++)
        {
            if (position == PbtFourLevelGroupGeometry.RootPosition && GroupKey.BitDepth != 0) continue;
            if ((Availability & (1u << position)) == 0) continue;
            yield return new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(GroupKey, position), _payload.AsSpan(_offsets[position], _lengths[position]));
        }
    }

    private void ValidatePosition(int position)
    {
        if ((uint)position >= PbtFourLevelGroupGeometry.PositionCount
            || (position == PbtFourLevelGroupGeometry.RootPosition && GroupKey.BitDepth != 0))
            throw new ArgumentOutOfRangeException(nameof(position));
    }
}
