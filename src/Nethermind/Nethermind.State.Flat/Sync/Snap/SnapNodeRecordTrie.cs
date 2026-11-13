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
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// The trie of one snap range, the state trie when <paramref name="address"/> is null, otherwise that account's storage trie,
/// built by <see cref="NodeRecordBulkSet"/> as node records in a temporary buffer and persisted from there.
/// </summary>
/// <remarks>
/// The buffer is rented, on the bulk set, from a pool for its number of entries, and returned on <see cref="Dispose"/>.
/// The proof's nodes, as the boundary fill trimmed them, are written to it first, so the bulk set starts from them.
/// </remarks>
internal sealed class SnapNodeRecordTrie(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, Hash256? address, bool enableDoubleWriteCheck)
    : INodeRecordStore, IDisposable
{
    private const int MaxPooledPerCategory = 16;

    // An entry writes some 200 bytes of records, its leaf and a share of the branches above it, besides up to some 16 KiB of proof.
    // A larger range spills into more generations, which a returned buffer frees, so a pooled one keeps only its first.
    private static readonly (int MaxEntries, int GenerationBytes)[] Categories =
        [(16, 32 * 1024), (256, 128 * 1024), (4096, 1024 * 1024), (int.MaxValue, 4 * 1024 * 1024)];

    private static readonly ConcurrentQueue<PrivateNodeRecordBuffer>[] Pools = [.. Categories.Select(static _ => new ConcurrentQueue<PrivateNodeRecordBuffer>())];

    private PrivateNodeRecordBuffer? _buffer;
    private int _category;
    private TrieNode? _proofRoot;
    private ulong _proofRootPointer;
    private (ValueHash256 Hash, ulong Pointer) _root = (ValueKeccak.EmptyTreeHash, 0);

    public Hash256 RootHash { get; private set; } = Keccak.EmptyTreeHash;

    public void SetRootFromProof(TrieNode root) => _proofRoot = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = address is null ? reader.TryLoadStateRlp(path, ReadFlags.None) : reader.TryLoadStorageRlp(address, path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    /// <summary>Sets the entries over the proof's nodes and computes the root hash.</summary>
    /// <exception cref="MissingTrieNodeException">An entry reaches a node that is neither in the proof nor persisted.</exception>
    public void BulkSet<TEntry>(IReadOnlyList<TEntry> entries) where TEntry : ISnapEntry
    {
        _category = 0;
        while (entries.Count > Categories[_category].MaxEntries) _category++;
        _buffer =Pools[_category].TryDequeue(out PrivateNodeRecordBuffer? pooled) ? pooled : new PrivateNodeRecordBuffer(Categories[_category].GenerationBytes);
        if (_proofRoot is not null)
        {
            TreePath path = TreePath.Empty;
            _proofRootPointer = WriteProofNode(_proofRoot, ref path);
            _root = (_proofRoot.Keccak!.ValueHash256, _proofRootPointer);
        }

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkSetEntries = ISnapTree<TEntry>.ToBulkSetEntries(entries);
        _root = NodeRecordBulkSet.BulkSet(this, address, _root, bulkSetEntries, canParallelize: true);
        RootHash = new Hash256(_root.Hash);
    }

    /// <summary>Persists the trie's nodes as <see cref="SnapUpperBoundAdapter"/> commits a stitched trie: each whose keys all lie at or below <paramref name="upperBound"/> and whose children are all persisted.</summary>
    /// <remarks>
    /// The nodes are reached from the root by their records' child pointers. A child without a pointer is not written by this range,
    /// so is checked in persistence. When the root is persisted, so is marked the proof's root, as a trie node commit marks it.
    /// </remarks>
    /// <param name="skipRoot">Whether the root is left out.</param>
    public void Commit(in ValueHash256 upperBound, bool skipRoot)
    {
        if (_root.Pointer == 0) return;

        if (Persist(_root.Pointer, TreePath.Empty, upperBound, skipRoot) && _proofRoot is not null) _proofRoot.IsPersisted = true;
    }

    public void Dispose()
    {
        if (_buffer is null) return;

        _buffer.Clear();
        if (Pools[_category].Count < MaxPooledPerCategory) Pools[_category].Enqueue(_buffer);
        else _buffer.Dispose();
        _buffer = null;
    }

    public bool TryLocate(ulong pointer, out NodeRecord record) => _buffer!.TryLocate(pointer, out record);

    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => _buffer!.StartWriteBatch(alignedTotalLength);

    public bool TryLoad<TArg>(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
        address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None, action, arg)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None, action, arg);

    /// <summary>Writes the record of the proof node <paramref name="node"/> at <paramref name="path"/>, after those of the proof nodes below it, linking them by pointer.</summary>
    /// <returns>The record's pointer, or 0 when the node is embedded in its parent.</returns>
    private ulong WriteProofNode(TrieNode node, ref TreePath path)
    {
        int childCount = node.IsBranch ? TrieNode.BranchesCount : node.IsExtension ? 1 : 0;
        Span<ulong> childPointers = stackalloc ulong[childCount];
        for (int i = 0; i < childCount; i++)
        {
            childPointers[i] = 0;
            if (!node.TryGetDirtyChild(i, out TrieNode? child)) continue;

            int previousLength = node.AppendChildPath(ref path, i);
            childPointers[i] = WriteProofNode(child, ref path);
            path.TruncateMut(previousLength);
        }

        node.ResolveKey(NullTrieNodeResolver.Instance, ref path, canBeParallel: false);
        if (node.Keccak is null) return 0;

        ReadOnlySpan<byte> rlp = node.FullRlp.AsSpan();
        if (!NodeRecordCodec.TryDecode(rlp, out NodeRecordKind kind, out ushort hashMask)) throw new TrieNodeException("Invalid proof node.", path, node.Keccak);

        using INodeRecordWriteBatch batch = _buffer!.StartWriteBatch(NodeRecordCodec.AlignedLength(NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length)));
        Span<byte> record = batch.Bytes;
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, node.Keccak.ValueHash256, 0, record);
        for (int i = 0; i < childCount; i++)
        {
            if (childPointers[i] == 0) continue;

            int position = BitOperations.PopCount(hashMask & ((1u << i) - 1));
            BinaryPrimitives.WriteUInt64LittleEndian(record[(NodeRecordCodec.HeaderLength + NodeRecordCodec.PointerLength * position)..], childPointers[i]);
        }

        return batch.BasePointer;
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
}
