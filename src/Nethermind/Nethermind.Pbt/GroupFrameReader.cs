// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;

namespace Nethermind.Pbt;

/// <summary>Reads a node group that is physically stored, as one frame of a fold.</summary>
/// <remarks>
/// A frame reader only ever wraps a stored payload: never construct one for a group that does not exist. A group the
/// boundary node proves absent is folded through <see cref="AbsentGroupFrame{TKey, TPath}"/> instead, and the tree
/// root's group, the only one whose existence is learned from the store, is probed with <see cref="TryLoad"/>.
/// </remarks>
internal struct GroupFrameReader<TKey, TPath> : IGroupFrame<TKey, TPath>, IDisposable
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    private RefCountingMemory? _lease;
    private readonly int _offsetTable;
    private readonly uint _stored;

    /// <summary>Loads the group stored at <paramref name="path"/>, keyed by <paramref name="groupHash"/>.</summary>
    /// <exception cref="InvalidDataException">The store holds no group at <paramref name="path"/>.</exception>
    internal GroupFrameReader(IPbtStore store, scoped in PbtTraversalPath path, in ValueHash256 groupHash)
        : this(store.GetNodeGroup(path, groupHash) ?? throw new InvalidDataException("A referenced PBT node group is missing."), path.BitDepth) { }

    /// <summary>Takes ownership of <paramref name="lease"/>, a group payload stored at depth <paramref name="bitDepth"/>.</summary>
    internal GroupFrameReader(RefCountingMemory lease, int bitDepth)
    {
        BitDepth = bitDepth;
        _lease = lease;
        try
        {
            ReadOnlySpan<byte> payload = lease.GetSpan();
            uint availability = PbtNodeGroupCodec.ReadAvailability(payload);
            Debug.Assert(bitDepth == 0 || (availability & (1u << PbtFourLevelGroupGeometry.RootPosition)) == 0, "Only the root group stores the root position.");
            _offsetTable = payload.Length - PbtNodeGroupCodec.GetTrailerLength(availability, payload);
            _stored = availability;
        }
        catch
        {
            ((IDisposable)lease).Dispose();
            throw;
        }
    }

    /// <summary>Loads the group stored at <paramref name="path"/>, or reports that the store holds none.</summary>
    /// <remarks>
    /// Only the tree root's group may be missing, when the tree is empty. Its absence cannot be derived from
    /// <paramref name="groupHash"/>, which may be stale or default when unknown, so the store is asked.
    /// </remarks>
    internal static bool TryLoad(IPbtStore store, scoped in PbtTraversalPath path, in ValueHash256 groupHash,
        out GroupFrameReader<TKey, TPath> reader)
    {
        RefCountingMemory? lease = store.GetNodeGroup(path, groupHash);
        reader = lease is null ? default : new(lease, path.BitDepth);
        return lease is not null;
    }

    public int BitDepth { get; }

    /// <inheritdoc/>
    public readonly int PayloadLength => _lease!.GetSpan().Length;

    /// <inheritdoc/>
    public readonly long DescendantBytes(int slot)
    {
        ReadOnlySpan<byte> payload = _lease!.GetSpan();
        ushort descendantMask = PbtNodeGroupCodec.ReadDescendantMask(payload);
        return (descendantMask & (1 << slot)) == 0 ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(payload, descendantMask, slot);
    }

    /// <inheritdoc/>
    public readonly ushort DescendantMask => PbtNodeGroupCodec.ReadDescendantMask(_lease!.GetSpan());

    /// <inheritdoc/>
    public readonly ReadOnlyMemory<byte> GetEncoding(int position)
    {
        if ((_stored & (1u << position)) == 0) return default;
        ReadOnlySpan<byte> payload = _lease!.GetSpan();
        int rank = RankOf(position);
        int start = EntryStart(payload, rank);
        return _lease.Memory[start..EntryEnd(payload, rank)];
    }

    /// <inheritdoc/>
    public readonly void CopyRange(PbtNodeGroupWriter<TPath> writer, int startPosition, int endPosition)
    {
        uint copied = _stored & ((1u << endPosition) - 1) & ~((1u << startPosition) - 1);
        if (copied == 0) return;
        ReadOnlySpan<byte> payload = _lease!.GetSpan();
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
    internal readonly TrieUpdater<TKey, TPath>.BoundaryNode TakeRoot(in ValueHash256 groupHash)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(PbtFourLevelGroupGeometry.RootPosition);
        if (encoding.IsEmpty) return default;
        PbtNodeReader root = PbtNodeReader.FromValidated(encoding.Span);
        return root.IsLeaf
            ? new TrieUpdater<TKey, TPath>.BoundaryNode(encoding, groupHash)
            : new TrieUpdater<TKey, TPath>.BoundaryNode(encoding, BitDepth, Blake3Hash.Hash(root.Preimage));
    }

    public void Dispose()
    {
        ((IDisposable?)_lease)?.Dispose();
        _lease = null;
    }

    /// <summary>Releases the actual mutable frame, including payloads loaded after this scope was opened.</summary>
    internal readonly ref struct Scope(ref GroupFrameReader<TKey, TPath> reader) : IDisposable
    {
        private readonly ref GroupFrameReader<TKey, TPath> _reader = ref reader;

        public void Dispose() => _reader.Dispose();
    }
}
