// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <param name="writer">The destination writer, passed by reference because it is mutable.</param>
    /// <param name="groupKey">The four-level key identifying the group.</param>
    /// <param name="nodes">Nodes belonging to this group, each with its complete canonical path and encoding.</param>
    /// <param name="descendantBytes">The summed payload lengths of the groups physically stored below each boundary slot, or empty for none.</param>
    public static void Encode<TPath>(ref BufferWriter writer, TPath groupKey, IReadOnlyList<PbtNodeRecord> nodes, ReadOnlySpan<long> descendantBytes)
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
    /// <inheritdoc cref="Encode{TPath}(ref BufferWriter, TPath, IReadOnlyList{PbtNodeRecord}, ReadOnlySpan{long})"/>
    public static byte[] Encode<TPath>(TPath groupKey, IReadOnlyList<PbtNodeRecord> nodes, ReadOnlySpan<long> descendantBytes)
        where TPath : struct, IPbtNodePath<TPath>
    {
        int capacity = PbtNodeGroupCodec.HeaderLength + PbtNodeGroupCodec.MaxTrailerLength;
        foreach (PbtNodeRecord record in nodes) capacity += record.Encoding.Length;
        BufferWriter writer = new(new byte[capacity]);
        Encode(ref writer, groupKey, nodes, descendantBytes);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a canonical group payload without descendants into memory rented from <paramref name="provider"/>.</summary>
    public static RefCountingMemory EncodeToMemory<TPath>(TPath groupKey, IReadOnlyList<PbtNodeRecord> nodes, IRefCountingMemoryProvider provider)
        where TPath : struct, IPbtNodePath<TPath>
    {
        BufferWriter writer = new(provider);
        try
        {
            Encode(ref writer, groupKey, nodes, default);
            return writer.Detach()!;
        }
        finally
        {
            writer.Dispose();
        }
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

/// <summary>Writes directly to a fixed or rented-growable buffer.</summary>
/// <remarks><see cref="Detach"/> transfers a rented buffer; <see cref="Dispose"/> releases an undetached one.</remarks>
internal ref struct BufferWriter
{
    private readonly IRefCountingMemoryProvider? _provider;
    private RefCountingMemory? _memory;
    private Span<byte> _buffer;
    private int _written;

    /// <summary>Writes into <paramref name="destination"/> and no further; <see cref="Detach"/> is unavailable.</summary>
    public BufferWriter(Span<byte> destination) => _buffer = destination;

    /// <summary>Rents from <paramref name="provider"/> on the first write and grows as needed.</summary>
    public BufferWriter(IRefCountingMemoryProvider provider) => _provider = provider;

    /// <summary>The bytes committed so far.</summary>
    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer[.._written];

    /// <summary>Gets room for at least <paramref name="sizeHint"/> bytes; the next call may invalidate the span.</summary>
    public Span<byte> GetSpan(int sizeHint)
    {
        Debug.Assert(sizeHint >= 0);
        if (_buffer.Length - _written < sizeHint) Grow(sizeHint);
        return _buffer[_written..];
    }

    /// <summary>Commits <paramref name="count"/> of the bytes <see cref="GetSpan"/> last handed out.</summary>
    public void Advance(int count)
    {
        Debug.Assert((uint)count <= (uint)(_buffer.Length - _written), "the writer advances only over room it handed out");
        _written += count;
    }

    /// <summary>Appends <paramref name="source"/> verbatim.</summary>
    public void Write(ReadOnlySpan<byte> source)
    {
        source.CopyTo(GetSpan(source.Length));
        _written += source.Length;
    }

    /// <summary>Transfers the written rented buffer to the caller, or returns <c>null</c> when empty.</summary>
    /// <exception cref="InvalidOperationException">The writer wrote into a caller's buffer, which it cannot hand over.</exception>
    public RefCountingMemory? Detach()
    {
        if (_written == 0)
        {
            Dispose();
            return null;
        }

        if (_memory is null) throw new InvalidOperationException("The writer has no buffer of its own to hand over");

        RefCountingMemory memory = _memory;
        memory.Shrink(_written);
        _memory = null;
        _buffer = default;
        _written = 0;
        return memory;
    }

    /// <summary>Releases an undetached rented buffer.</summary>
    public void Dispose()
    {
        ((IDisposable?)_memory)?.Dispose();
        _memory = null;
        _buffer = default;
        _written = 0;
    }

    private void Grow(int sizeHint)
    {
        if (_provider is null) throw new InvalidOperationException($"A writer over {_buffer.Length} bytes has no room for {sizeHint} more past {_written}");

        int required = _written + sizeHint;
        int capacity = Math.Max(required, _buffer.Length * 2);
        RefCountingMemory grown = _provider.Rent(capacity);
        WrittenSpan.CopyTo(grown.GetSpan());
        ((IDisposable?)_memory)?.Dispose();
        _memory = grown;
        _buffer = grown.GetSpan();
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
