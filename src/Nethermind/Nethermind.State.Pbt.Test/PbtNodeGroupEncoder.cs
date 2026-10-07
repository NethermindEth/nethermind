// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Memory;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>Batch node-group encoder used as the oracle for <see cref="PbtNodeGroupWriter{TPath}"/> output.</summary>
internal static class PbtNodeGroupEncoder
{
    private const int MaxOffset = ushort.MaxValue;

    /// <summary>
    /// Validates and writes a canonical group payload directly through <paramref name="writer"/>.
    /// </summary>
    /// <remarks>
    /// Validation completes before the first write. Node encodings are then copied once in position
    /// order, followed by the offset table, availability bitmap and subtree size.
    /// </remarks>
    /// <param name="writer">The destination writer.</param>
    /// <param name="groupKey">The four-level key identifying the group.</param>
    /// <param name="nodes">Nodes belonging to this group, each with its complete canonical path and encoding.</param>
    /// <param name="descendantBytes">The summed payload lengths of the groups physically stored below each boundary slot, or empty for none.</param>
    public static void Encode<TPath>(IBufferWriter<byte> writer, TPath groupKey, IReadOnlyList<PbtNodeRecord> nodes, ReadOnlySpan<long> descendantBytes)
        where TPath : struct, IPbtNodePath<TPath>
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ushort descendantMask = PbtNodeGroupCodec.DescendantMask(descendantBytes, ushort.MaxValue);
        ValidateGroupKey(groupKey);
        if (nodes.Count == 0) throw new InvalidDataException("A PBT node group cannot be empty.");
        if (nodes.Count > PbtFourLevelGroupGeometry.PositionCount) throw new InvalidDataException("A PBT node group has too many nodes.");

        Span<int> recordIndices = stackalloc int[PbtFourLevelGroupGeometry.PositionCount];
        recordIndices.Fill(-1);
        uint availability = 0;
        uint seenPositions = 0;
        int entriesLength = 0;
        for (int index = 0; index < nodes.Count; index++)
        {
            PbtNodeRecord record = nodes[index] ?? throw new InvalidDataException("A PBT node group contains a null record.");
            PbtNodeGroupLocation<PbtStorageNodePath> location = PbtTestPaths.Locate(record.Path);
            if (!PbtNodePathOperations.Equal(location.GroupKey, groupKey)) throw new InvalidDataException("Node does not belong to the group key.");
            if ((uint)location.Position >= PbtFourLevelGroupGeometry.PositionCount
                || (location.Position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0))
                throw new InvalidDataException("The group contains a reserved node position.");

            uint bit = 1u << location.Position;
            if ((seenPositions & bit) != 0) throw new InvalidDataException("Duplicate node position in group.");
            seenPositions |= bit;
            ReadOnlySpan<byte> encoding = record.Encoding.Span;
            PbtNodeCodec.ThrowIfNotExact(encoding);
            PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
            ValidateNodePath(node, record.Path);
            if (PbtNodeGroupCodec.ShouldOmit(location.Position, encoding)) continue;
            entriesLength = checked(entriesLength + encoding.Length);
            if (entriesLength > MaxOffset) throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
            recordIndices[location.Position] = index;
            availability |= bit;
        }

        if (availability == 0) throw new InvalidDataException("A PBT node group cannot be empty.");
        writer.Write(PbtNodeGroupCodec.Header);
        Span<ushort> offsets = stackalloc ushort[PbtFourLevelGroupGeometry.PositionCount];
        int offset = 0;
        for (int position = 0; position < PbtFourLevelGroupGeometry.PositionCount; position++)
        {
            int recordIndex = recordIndices[position];
            if (recordIndex < 0) continue;
            offsets[position] = (ushort)offset;
            ReadOnlySpan<byte> encoding = nodes[recordIndex].Encoding.Span;
            writer.Write(encoding);
            offset = checked(offset + encoding.Length);
        }

        int trailerLength = PbtNodeGroupCodec.GetTrailerLength(availability, descendantMask, descendantBytes);
        PbtNodeGroupCodec.WriteFooter(writer.GetSpan(trailerLength), offsets, availability, descendantMask, descendantBytes);
        writer.Advance(trailerLength);
    }

    /// <summary>Encodes a canonical group payload into an exactly sized array.</summary>
    /// <inheritdoc cref="Encode{TPath}(IBufferWriter{byte}, TPath, IReadOnlyList{PbtNodeRecord}, ReadOnlySpan{long})"/>
    public static byte[] Encode<TPath>(TPath groupKey, IReadOnlyList<PbtNodeRecord> nodes, ReadOnlySpan<long> descendantBytes)
        where TPath : struct, IPbtNodePath<TPath>
    {
        int capacity = PbtNodeGroupCodec.HeaderLength + PbtNodeGroupCodec.MaxTrailerLength;
        foreach (PbtNodeRecord record in nodes) capacity += record.Encoding.Length;
        ArrayBufferWriter<byte> writer = new(capacity);
        Encode(writer, groupKey, nodes, descendantBytes);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a canonical group payload without descendants into memory rented from <paramref name="provider"/>.</summary>
    public static RefCountingMemory EncodeToMemory<TPath>(TPath groupKey, IReadOnlyList<PbtNodeRecord> nodes, IRefCountingMemoryProvider provider)
        where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] encoded = Encode(groupKey, nodes, default);
        RefCountingMemory memory = provider.Rent(encoded.Length);
        encoded.CopyTo(memory.GetSpan());
        return memory;
    }

    private static void ValidateNodePath<TPath>(PbtNodeReader node, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        if (node.IsLeaf)
        {
            if (path.BitDepth != 0) throw new InvalidDataException("A PBT leaf entry is only valid as the tree root.");
            return;
        }
        if (!MatchesInlineLeaves(node, path)) throw new InvalidDataException("PBT leaf does not match its group position.");
    }

    private static bool MatchesInlineLeaves<TPath>(PbtNodeReader node, TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        byte[] pathBytes = new byte[PbtBitPrefix.ByteCount(path.BitDepth)];
        PbtNodePathOperations.CopyTo(path, pathBytes);
        byte[] keyPrefix = pathBytes[..PbtNodeCodec.InlineKeyOffset(path.BitDepth)];
        return (node.LeftKeyPostfix.IsEmpty || StartsWith([.. keyPrefix, .. node.LeftKeyPostfix], path))
            && (node.RightKeyPostfix.IsEmpty || StartsWith([.. keyPrefix, .. node.RightKeyPostfix], path));
    }

    private static bool StartsWith<TPath>(ReadOnlySpan<byte> key, TPath path) where TPath : struct, IPbtNodePath<TPath> =>
        key.Length * 8 >= path.BitDepth && PbtTestPaths.Prefix<TPath>(key, path.BitDepth).Equals(path);

    private static void ValidateGroupKey<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
    }
}

/// <summary>An owned canonical path/node record.</summary>
internal sealed class PbtNodeRecord
{
    private readonly byte[] _encoding;

    public PbtNodeRecord(PbtStorageNodePath path, ReadOnlySpan<byte> encoding)
    {
        Path = path;
        _encoding = encoding.ToArray();
    }

    /// <summary>Gets the complete canonical path.</summary>
    public PbtStorageNodePath Path { get; }

    /// <summary>Gets the exact canonical node encoding.</summary>
    public ReadOnlyMemory<byte> Encoding => _encoding;
}
