// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// The node records of one snap range's trie, the state trie when <paramref name="address"/> is null, otherwise that account's
/// storage trie, built by <see cref="NodeRecordBulkSet"/> in a temporary buffer and persisted from there.
/// </summary>
/// <remarks>
/// The buffer is rented, on the bulk set, from a pool for its number of entries, and returned on <see cref="Dispose"/>.
/// The proof's nodes are written to it first, from their RLP as received or re-encoded where the boundary walk trimmed them,
/// so the bulk set starts from them.
/// </remarks>
internal sealed class SnapNodeRecordStore(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, Hash256? address, bool enableDoubleWriteCheck)
    : INodeRecordStore, IDisposable
{
    private const int MaxPooledPerCategory = 16;

    private static readonly ConcurrentQueue<PrivateNodeRecordBuffer>[] Pools = [.. Enum.GetValues<SizeCategory>().Select(static _ => new ConcurrentQueue<PrivateNodeRecordBuffer>())];

    private PrivateNodeRecordBuffer? _buffer;
    private SizeCategory _sizeCategory;
    private IByteArrayList? _proofs;
    private IReadOnlyList<SnapProofNode>? _proofNodes;
    private ulong _proofRootPointer;
    private (ValueHash256 Hash, ulong Pointer) _root = (ValueKeccak.EmptyTreeHash, 0);

    public Hash256 RootHash { get; private set; } = Keccak.EmptyTreeHash;

    /// <inheritdoc cref="ISnapTree{TEntry}.SetRootFromProof"/>
    public void SetRootFromProof(IByteArrayList proofs, IReadOnlyList<SnapProofNode> nodes) => (_proofs, _proofNodes) = (proofs, nodes);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = address is null ? reader.TryLoadStateRlp(path, ReadFlags.None) : reader.TryLoadStorageRlp(address, path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    /// <summary>Sets the entries over the proof's nodes and computes the root hash.</summary>
    /// <exception cref="MissingTrieNodeException">An entry reaches a node that is neither in the proof nor persisted.</exception>
    public void BulkSet<TEntry>(IReadOnlyList<TEntry> entries) where TEntry : ISnapEntry
    {
        _buffer = RentBuffer(entries.Count);
        if (_proofNodes is not null)
        {
            _root = WriteProofNodes(_proofs!, _proofNodes);
            _proofRootPointer = _root.Pointer;
        }

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkSetEntries = ISnapTree<TEntry>.ToBulkSetEntries(entries);
        _root = NodeRecordBulkSet.BulkSet(this, address, _root, bulkSetEntries, canParallelize: true);
        RootHash = new Hash256(_root.Hash);
    }

    /// <summary>Persists the trie's nodes as <see cref="SnapUpperBoundAdapter"/> commits a stitched trie: each whose keys all lie at or below <paramref name="upperBound"/> and whose children are all persisted.</summary>
    /// <remarks>
    /// The nodes are reached from the root by their records' child pointers. A child without a pointer is not written by this range,
    /// so is checked in persistence.
    /// </remarks>
    /// <param name="skipRoot">Whether the root is left out.</param>
    /// <returns>Whether the proof's root is persisted; true without a proof.</returns>
    public bool Commit(in ValueHash256 upperBound, bool skipRoot)
    {
        bool isRootWritten = _root.Pointer != 0 && Persist(_root.Pointer, TreePath.Empty, upperBound, skipRoot);
        if (_proofRootPointer == 0 || isRootWritten) return true;

        // The range never writes a leaf root of the proof, which a range holding its key may have, as stitching checks.
        _buffer!.TryLocate(_proofRootPointer, out NodeRecord proofRoot);
        return proofRoot.Kind == NodeRecordKind.Leaf && IsPersisted(TreePath.Empty, proofRoot.Hash);
    }

    public void Dispose()
    {
        if (_buffer is null) return;

        _buffer.Clear();
        ConcurrentQueue<PrivateNodeRecordBuffer> pool = Pools[(int)_sizeCategory];
        if (pool.Count < MaxPooledPerCategory) pool.Enqueue(_buffer);
        else _buffer.Dispose();
        _buffer = null;
    }

    public bool TryLocate(ulong pointer, out NodeRecord record) => _buffer!.TryLocate(pointer, out record);

    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => _buffer!.StartWriteBatch(alignedTotalLength);

    public bool TryLoad<TArg>(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
        address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None, action, arg)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None, action, arg);

    /// <summary>Rents the buffer of a range of <paramref name="entryCount"/> entries from the pool of its <see cref="SizeCategory"/>.</summary>
    /// <remarks>
    /// An entry writes some 200 bytes of records, its leaf and a share of the branches above it, besides up to some 16 KiB of proof.
    /// A larger range spills into more generations, which a returned buffer frees, so a pooled one keeps only its first.
    /// </remarks>
    private PrivateNodeRecordBuffer RentBuffer(int entryCount)
    {
        (_sizeCategory, int generationBytes) = entryCount switch
        {
            <= 16 => (SizeCategory.Tiny, 32 * 1024),
            <= 256 => (SizeCategory.Small, 128 * 1024),
            <= 4096 => (SizeCategory.Medium, 1024 * 1024),
            _ => (SizeCategory.Large, 4 * 1024 * 1024),
        };
        return Pools[(int)_sizeCategory].TryDequeue(out PrivateNodeRecordBuffer? pooled) ? pooled : new PrivateNodeRecordBuffer(generationBytes);
    }

    /// <summary>Writes the records of the proof's <paramref name="nodes"/>, each after those of its children, which it references by pointer.</summary>
    /// <returns>The root's hash and pointer.</returns>
    private (ValueHash256 Hash, ulong Pointer) WriteProofNodes(IByteArrayList proofs, IReadOnlyList<SnapProofNode> nodes)
    {
        using ArrayPoolListRef<(ValueHash256 Hash, ulong Pointer)> written = new(nodes.Count, nodes.Count);
        Span<(ValueHash256 Hash, ulong Pointer)> records = written.AsSpan();
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            records[i] = WriteProofNode(proofs, nodes, i, records);
        }

        return records[0];
    }

    /// <summary>Writes the record of proof node <paramref name="index"/>, less its cleared children and linked to the records of its proof children.</summary>
    /// <param name="written">The records of the nodes after <paramref name="index"/>, among them its children.</param>
    private (ValueHash256 Hash, ulong Pointer) WriteProofNode(IByteArrayList proofs, IReadOnlyList<SnapProofNode> nodes, int index, ReadOnlySpan<(ValueHash256 Hash, ulong Pointer)> written)
    {
        SnapProofNode node = nodes[index];
        ReadOnlySpan<byte> rlp = proofs[node.ProofIndex];
        NodeRecordCodec.Decode(rlp, out NodeRecordKind kind, out ushort hashMask);

        bool isTrimmed = node.ClearedChildren != 0;
        for (int i = index + 1; !isTrimmed && i < nodes.Count; i++)
        {
            isTrimmed = nodes[i].ParentIndex == index;
        }

        if (!isTrimmed) return WriteRecord(rlp, kind, hashMask, []);

        Span<byte> original = stackalloc byte[NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length)];
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, default, 0, original);
        NodeRecordRef view = new(original);
        bool isBranch = kind is NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch;
        Span<ChildRef> children = stackalloc ChildRef[isBranch ? TrieNode.BranchesCount : 1];
        if (isBranch) view.CopyChildren(children);
        else children[0] = view.Child(0);

        for (int mask = node.ClearedChildren; mask != 0; mask &= mask - 1)
        {
            children[BitOperations.TrailingZeroCount(mask)] = default;
        }

        for (int i = index + 1; i < nodes.Count; i++)
        {
            if (nodes[i].ParentIndex == index) children[nodes[i].ChildIndex] = ChildRef.ForHash(written[i].Hash, written[i].Pointer);
        }

        Span<byte> key = stackalloc byte[view.KeyLength(0)];
        view.CopyKey(0, key);
        Span<byte> trimmed = stackalloc byte[isBranch ? NodeRlp.BranchLength(children) : NodeRlp.ExtensionLength(key, children[0])];
        if (isBranch) NodeRlp.WriteBranch(children, trimmed);
        else NodeRlp.WriteExtension(key, children[0], trimmed);

        NodeRecordCodec.Decode(trimmed, out NodeRecordKind trimmedKind, out ushort trimmedHashMask);
        return WriteRecord(trimmed, trimmedKind, trimmedHashMask, children);
    }

    /// <summary>Writes the record of <paramref name="rlp"/>, pointing to the records of the <paramref name="children"/> that have one.</summary>
    private (ValueHash256 Hash, ulong Pointer) WriteRecord(ReadOnlySpan<byte> rlp, NodeRecordKind kind, ushort hashMask, ReadOnlySpan<ChildRef> children)
    {
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        using INodeRecordWriteBatch batch = _buffer!.StartWriteBatch(NodeRecordCodec.AlignedLength(NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length)));
        Span<byte> record = batch.Bytes;
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, hash, 0, record);
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i].Kind != ChildRefKind.Hash || children[i].Pointer == 0) continue;

            int position = BitOperations.PopCount(hashMask & ((1u << i) - 1));
            BinaryPrimitives.WriteUInt64LittleEndian(record[(NodeRecordCodec.HeaderLength + NodeRecordCodec.PointerLength * position)..], children[i].Pointer);
        }

        return (hash, batch.BasePointer);
    }

    /// <summary>Persists the persistable nodes below the record at <paramref name="pointer"/>, then the record's node when it is persistable too.</summary>
    /// <returns>Whether the node is persisted.</returns>
    private bool Persist(ulong pointer, in TreePath path, in ValueHash256 upperBound, bool skip)
    {
        _buffer!.TryLocate(pointer, out NodeRecord record);
        NodeRecordRef view = record.View;
        bool isBranch = view.Kind is NodeRecordKind.HashedBranch or NodeRecordKind.InlineBranch;
        Span<byte> key = stackalloc byte[BulkSetWorkingState.MaxKeyNibbles];
        view.CopyKey(0, key);
        TreePath keyEnd = path.Append(key[..view.KeyLength(0)]);

        // A child with a record is new or from the proof, and persisted on its own terms, even when this node is not.
        bool childrenPersisted = true;
        for (int mask = view.HashMask; mask != 0; mask &= mask - 1)
        {
            int index = BitOperations.TrailingZeroCount(mask);
            ulong childPointer = view.Child(index).Pointer;
            if (childPointer != 0) childrenPersisted &= Persist(childPointer, isBranch ? keyEnd.Append(index) : keyEnd, upperBound, skip: false);
        }

        // Only the root can be a leaf of the proof, which references the others by hash. Its key is outside the range, so
        // the range holding it persists it.
        bool isProofLeaf = pointer == _proofRootPointer && view.Kind == NodeRecordKind.Leaf;
        if (skip || isProofLeaf || !childrenPersisted || keyEnd.ToUpperBoundPath() > upperBound || !AreOtherChildrenPersisted(view, keyEnd, isBranch)) return false;

        Write(path, view.Rlp);
        return true;
    }

    /// <summary>Whether every child referenced by hash without a record, which this range does not write, is persisted.</summary>
    private bool AreOtherChildrenPersisted(NodeRecordRef view, in TreePath keyEnd, bool isBranch)
    {
        for (int mask = view.HashMask; mask != 0; mask &= mask - 1)
        {
            int index = BitOperations.TrailingZeroCount(mask);
            ChildRef child = view.Child(index);
            if (child.Pointer == 0 && !IsPersisted(isBranch ? keyEnd.Append(index) : keyEnd, child.Data)) return false;
        }

        return true;
    }

    private void Write(in TreePath path, ReadOnlySpan<byte> rlp)
    {
        if (address is null)
        {
            if (enableDoubleWriteCheck && reader.TryLoadStateRlp(path, ReadFlags.None) is not null) throw new InvalidOperationException($"Double state rlp write. {path}");

            writeBatch.SetStateTrieNode(path, rlp);
        }
        else
        {
            if (enableDoubleWriteCheck && reader.TryLoadStorageRlp(address, path, ReadFlags.None) is not null) throw new InvalidOperationException($"Double storage rlp write. {address} {path}");

            writeBatch.SetStorageTrieNode(address, path, rlp);
        }
    }

    /// <summary>The size of a range's buffer, by the range's number of entries; each has a pool of its own.</summary>
    private enum SizeCategory
    {
        Tiny,
        Small,
        Medium,
        Large,
    }
}
