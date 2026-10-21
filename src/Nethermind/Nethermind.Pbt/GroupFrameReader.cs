// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

/// <summary>Reads a node group that is physically stored, as one frame of a fold.</summary>
/// <remarks>
/// A frame reader only ever wraps a stored payload: never construct one for a group that does not exist. A group the
/// boundary node proves absent is folded through <see cref="AbsentGroupFrame{TKey, TPath}"/> instead, and the tree
/// root's group is loaded through its direct reference or <see cref="IPbtStore.TryLoad"/>.
/// </remarks>
public struct GroupFrameReader<TKey, TPath> : IGroupFrame<TKey, TPath>, IDisposable
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    private RefCountingMemory? _lease;
    private ReadOnlyMemory<byte> _payload;
    private readonly PbtGroupLinks _links;
    private readonly int _offsetTable;
    private readonly uint _stored;

    /// <summary>Takes ownership of <paramref name="lease"/>, a group payload stored at depth <paramref name="bitDepth"/>.</summary>
    public GroupFrameReader(RefCountingMemory lease, int bitDepth) : this(lease.Memory, bitDepth, default, lease) { }

    /// <summary>Borrows a buffer record whose operation lease remains alive throughout this frame.</summary>
    public GroupFrameReader(PbtGroupRecord record) : this(record.Payload, record.Path.BitDepth, record.Links, null) { }

    private GroupFrameReader(ReadOnlyMemory<byte> memory, int bitDepth, PbtGroupLinks links, RefCountingMemory? lease)
    {
        BitDepth = bitDepth;
        _lease = lease;
        _payload = memory;
        _links = links;
        try
        {
            ReadOnlySpan<byte> payload = memory.Span;
            uint availability = PbtNodeGroupCodec.ReadAvailability(payload);
            Debug.Assert(bitDepth == 0 || (availability & (1u << PbtThreeLevelGroupGeometry.RootPosition)) == 0, "Only the root group stores the root position.");
            _offsetTable = payload.Length - PbtNodeGroupCodec.GetTrailerLength(availability, payload);
            _stored = availability;
        }
        catch
        {
            ((IDisposable?)lease)?.Dispose();
            throw;
        }
    }

    public int BitDepth { get; }

    /// <inheritdoc/>
    public readonly int PayloadLength => _payload.Span.Length;

    /// <inheritdoc/>
    public readonly long DescendantBytes(int slot)
    {
        ReadOnlySpan<byte> payload = _payload.Span;
        ushort descendantMask = PbtNodeGroupCodec.ReadDescendantMask(payload);
        return (descendantMask & (1 << slot)) == 0 ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(payload, descendantMask, slot);
    }

    /// <inheritdoc/>
    public readonly ushort DescendantMask => PbtNodeGroupCodec.ReadDescendantMask(_payload.Span);

    /// <inheritdoc/>
    public readonly ulong DescendantPointer(int slot) => (DescendantMask & (1 << slot)) == 0 ? 0 : _links[slot];

    /// <inheritdoc/>
    public readonly ReadOnlyMemory<byte> GetEncoding(int position)
    {
        if ((_stored & (1u << position)) == 0) return default;
        ReadOnlySpan<byte> payload = _payload.Span;
        int rank = RankOf(position);
        int start = EntryStart(payload, rank);
        return _payload[start..EntryEnd(payload, rank)];
    }

    /// <inheritdoc/>
    public readonly void CopyRange(PbtNodeGroupWriter<TPath> writer, int startPosition, int endPosition)
    {
        uint copied = _stored & ((1u << endPosition) - 1) & ~((1u << startPosition) - 1);
        if (copied == 0) return;
        ReadOnlySpan<byte> payload = _payload.Span;
        int firstRank = RankOf(BitOperations.TrailingZeroCount(copied));
        int count = BitOperations.PopCount(copied);
        writer.CopyRange(payload[EntryStart(payload, firstRank)..EntryEnd(payload, firstRank + count - 1)],
            payload.Slice(_offsetTable + firstRank * sizeof(ushort), count * sizeof(ushort)), copied);
    }

    /// <summary>The index of <paramref name="position"/>'s offset among the stored ones.</summary>
    private readonly int RankOf(int position) => BitOperations.PopCount(_stored & ((1u << position) - 1));

    private readonly int EntryStart(ReadOnlySpan<byte> payload, int rank) =>
        PbtNodeGroupCodec.HeaderLength + BinaryPrimitives.ReadUInt16LittleEndian(payload[(_offsetTable + rank * sizeof(ushort))..]);

    /// <summary>An entry ends where the next one starts, and the last one where the offset table starts.</summary>
    private readonly int EntryEnd(ReadOnlySpan<byte> payload, int rank) =>
        rank + 1 == BitOperations.PopCount(_stored) ? _offsetTable : EntryStart(payload, rank + 1);

    /// <inheritdoc/>
    public readonly uint StoredPositions => _stored;

    /// <summary>Takes the group's own root, whose hash is <paramref name="groupHash"/>.</summary>
    /// <remarks>
    /// A root branch is hashed from its own preimage. A root leaf cannot be, as no value is stored, so it takes
    /// <paramref name="groupHash"/>.
    /// </remarks>
    public readonly TrieUpdater<TKey, TPath>.BoundaryNode TakeRoot(in ValueHash256 groupHash)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(PbtThreeLevelGroupGeometry.RootPosition);
        if (encoding.IsEmpty) return default;
        return PbtNodeCodec.IsLeaf(encoding.Span)
            ? new TrieUpdater<TKey, TPath>.BoundaryNode(encoding, groupHash)
            : new TrieUpdater<TKey, TPath>.BoundaryNode(encoding, BitDepth, Blake3Hash.Hash(PbtBranchReader.FromValidated(encoding.Span).Preimage));
    }

    public void Dispose()
    {
        ((IDisposable?)_lease)?.Dispose();
        _lease = null;
        _payload = default;
    }

    /// <summary>Releases the actual mutable frame, including payloads loaded after this scope was opened.</summary>
    public readonly ref struct Scope(ref GroupFrameReader<TKey, TPath> reader) : IDisposable
    {
        private readonly ref GroupFrameReader<TKey, TPath> _reader = ref reader;

        public void Dispose() => _reader.Dispose();
    }
}
