// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

public class FlatTreeSyncStore(IPersistence persistence, ILogManager logManager) : ITreeSyncStore
{
    public bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        byte[]? data = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None);

        if (data is null) return false;

        // Rehash and verify
        ValueHash256 computedHash = ValueKeccak.Compute(data);
        return computedHash == hash;
    }

    public void SaveNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data)
    {
        StateId currentState;
        byte[]? existingData;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            currentState = reader.CurrentState;
            // Check if there's an existing node at this path
            existingData = address is null
                ? reader.TryLoadStateRlp(path, ReadFlags.None)
                : reader.TryLoadStorageRlp(address, path, ReadFlags.None);
        }

        // Deletion needed if there's existing data at this path
        bool needsDelete = existingData is not null;

        // Same state for from/to = no-op state transition, allows writing without state change
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(currentState, currentState, WriteFlags.DisableWAL);

        TrieNode node = new(NodeType.Unknown, data.ToArray(), isDirty: true);
        node.ResolveNode(NullTrieNodeResolver.Instance, path);

        if (address is null)
        {
            writeBatch.SetStateTrieNode(path, node);

            // For leaf nodes, also write the flat account entry
            if (node.IsLeaf)
            {
                ValueHash256 fullPath = path.Append(node.Key).Path;
                Account account = AccountDecoder.Instance.Decode(node.Value.Span)!;
                writeBatch.SetAccountRaw(fullPath.ToCommitment(), account);
            }

            if (needsDelete)
            {
                RequestStateDeletion(writeBatch, path, node);
            }
        }
        else
        {
            writeBatch.SetStorageTrieNode(address, path, node);

            // For leaf nodes, also write the flat storage entry
            if (node.IsLeaf)
            {
                ValueHash256 fullPath = path.Append(node.Key).Path;
                ReadOnlySpan<byte> value = node.Value.Span;
                byte[] toWrite = value.IsEmpty
                    ? StorageTree.ZeroBytes
                    : value.AsRlpValueContext().DecodeByteArray();
                writeBatch.SetStorageRaw(address, fullPath.ToCommitment(), SlotValue.FromSpanWithoutLeadingZero(toWrite));
            }

            if (needsDelete)
            {
                RequestStorageDeletion(writeBatch, address, path, node);
            }
        }
    }

    private static void RequestStateDeletion(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode node)
    {
        switch (node.NodeType)
        {
            case NodeType.Branch:
                RequestStateDeletionForBranch(writeBatch, path, node);
                break;
            case NodeType.Leaf:
                RequestStateDeletionForLeaf(writeBatch, path, node);
                break;
            case NodeType.Extension:
                RequestStateDeletionForExtension(writeBatch, path, node);
                break;
        }
    }

    private static void RequestStorageDeletion(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode node)
    {
        switch (node.NodeType)
        {
            case NodeType.Branch:
                RequestStorageDeletionForBranch(writeBatch, address, path, node);
                break;
            case NodeType.Leaf:
                RequestStorageDeletionForLeaf(writeBatch, address, path, node);
                break;
            case NodeType.Extension:
                RequestStorageDeletionForExtension(writeBatch, address, path, node);
                break;
        }
    }

    private static void RequestStateDeletionForBranch(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode node)
    {
        // For each null child in the branch, delete the subtree under that child
        for (int i = 0; i < 16; i++)
        {
            if (!node.IsChildNull(i)) continue;

            TreePath childPath = path.Append(i);
            (ValueHash256 fromPath, ValueHash256 toPath) = ComputeSubtreeRange(childPath);
            (TreePath fromTreePath, TreePath toTreePath) = ComputeTreePathRange(childPath);

            writeBatch.DeleteAccountRange(fromPath, toPath);
            writeBatch.DeleteStateTrieNodeRange(fromTreePath, toTreePath);
        }
    }

    private static void RequestStateDeletionForLeaf(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode node)
    {
        // A leaf at this path represents a single account. The presence of a leaf here means
        // any paths between the current position and the leaf's full path (exclusive of the leaf itself)
        // should be deleted. The leaf value itself is being written, so we don't delete it.

        // For healing purposes, we need to delete entries in the gap between path and the full leaf path.
        // The gap is: paths that share the prefix 'path' but are lexicographically before the leaf's full path.
        // Also delete paths that come after the leaf's full path but still share the 'path' prefix.

        TreePath fullPath = path.Append(node.Key);

        // Delete from path.Append(0,0,0...) to fullPath (exclusive)
        // and from fullPath+1 to path.Append(F,F,F...) (exclusive)
        // Simplification: Delete the range [path prefix padded with 0s, path prefix padded with Fs]
        // but exclude the fullPath itself which is being written

        // For the gap before the leaf
        if (fullPath.Path != path.Append(0, 64 - path.Length).Path)
        {
            ValueHash256 gapFrom = path.Append(0, 64 - path.Length).Path;
            // Compute path just before fullPath
            ValueHash256 gapTo = DecrementPath(fullPath.Path);
            if (gapTo.CompareTo(gapFrom) >= 0)
            {
                writeBatch.DeleteAccountRange(gapFrom, gapTo);
                writeBatch.DeleteStateTrieNodeRange(path.Append(0, 64 - path.Length), ComputeTreePathForHash(gapTo, 64));
            }
        }

        // For the gap after the leaf
        ValueHash256 afterLeaf = IncrementPath(fullPath.Path);
        ValueHash256 endOfSubtree = path.Append(0xF, 64 - path.Length).Path;
        if (afterLeaf.CompareTo(endOfSubtree) <= 0)
        {
            writeBatch.DeleteAccountRange(afterLeaf, endOfSubtree);
            writeBatch.DeleteStateTrieNodeRange(ComputeTreePathForHash(afterLeaf, 64), path.Append(0xF, 64 - path.Length));
        }
    }

    private static void RequestStateDeletionForExtension(IPersistence.IWriteBatch writeBatch, in TreePath path, TrieNode node)
    {
        // Extension node: the extension key covers a range of paths.
        // Similar logic to leaf - delete paths in the gap before and after the extension's path.

        TreePath extendedPath = path.Append(node.Key);

        // Delete from path.Append(0,0,0...) to extendedPath prefix (exclusive of extension's subtree)
        if (extendedPath.Path != path.Append(0, 64 - path.Length).Path)
        {
            ValueHash256 gapFrom = path.Append(0, 64 - path.Length).Path;
            ValueHash256 gapTo = DecrementPath(extendedPath.Append(0, 64 - extendedPath.Length).Path);
            if (gapTo.CompareTo(gapFrom) >= 0)
            {
                writeBatch.DeleteAccountRange(gapFrom, gapTo);
                writeBatch.DeleteStateTrieNodeRange(path.Append(0, 64 - path.Length), ComputeTreePathForHash(gapTo, 64));
            }
        }

        // Delete from end of extension's subtree to end of current subtree
        ValueHash256 afterExtension = IncrementPath(extendedPath.Append(0xF, 64 - extendedPath.Length).Path);
        ValueHash256 endOfSubtree = path.Append(0xF, 64 - path.Length).Path;
        if (afterExtension.CompareTo(endOfSubtree) <= 0)
        {
            writeBatch.DeleteAccountRange(afterExtension, endOfSubtree);
            writeBatch.DeleteStateTrieNodeRange(ComputeTreePathForHash(afterExtension, 64), path.Append(0xF, 64 - path.Length));
        }
    }

    private static void RequestStorageDeletionForBranch(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode node)
    {
        ValueHash256 addressHash = address.ValueHash256;
        for (int i = 0; i < 16; i++)
        {
            if (!node.IsChildNull(i)) continue;

            TreePath childPath = path.Append(i);
            (ValueHash256 fromPath, ValueHash256 toPath) = ComputeSubtreeRange(childPath);
            (TreePath fromTreePath, TreePath toTreePath) = ComputeTreePathRange(childPath);

            writeBatch.DeleteStorageRange(addressHash, fromPath, toPath);
            writeBatch.DeleteStorageTrieNodeRange(addressHash, fromTreePath, toTreePath);
        }
    }

    private static void RequestStorageDeletionForLeaf(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode node)
    {
        ValueHash256 addressHash = address.ValueHash256;
        TreePath fullPath = path.Append(node.Key);

        // Delete from path.Append(0,0,0...) to fullPath (exclusive)
        if (fullPath.Path != path.Append(0, 64 - path.Length).Path)
        {
            ValueHash256 gapFrom = path.Append(0, 64 - path.Length).Path;
            ValueHash256 gapTo = DecrementPath(fullPath.Path);
            if (gapTo.CompareTo(gapFrom) >= 0)
            {
                writeBatch.DeleteStorageRange(addressHash, gapFrom, gapTo);
                writeBatch.DeleteStorageTrieNodeRange(addressHash, path.Append(0, 64 - path.Length), ComputeTreePathForHash(gapTo, 64));
            }
        }

        // Delete from fullPath+1 to end of subtree
        ValueHash256 afterLeaf = IncrementPath(fullPath.Path);
        ValueHash256 endOfSubtree = path.Append(0xF, 64 - path.Length).Path;
        if (afterLeaf.CompareTo(endOfSubtree) <= 0)
        {
            writeBatch.DeleteStorageRange(addressHash, afterLeaf, endOfSubtree);
            writeBatch.DeleteStorageTrieNodeRange(addressHash, ComputeTreePathForHash(afterLeaf, 64), path.Append(0xF, 64 - path.Length));
        }
    }

    private static void RequestStorageDeletionForExtension(IPersistence.IWriteBatch writeBatch, Hash256 address, in TreePath path, TrieNode node)
    {
        ValueHash256 addressHash = address.ValueHash256;
        TreePath extendedPath = path.Append(node.Key);

        // Delete from path.Append(0,0,0...) to extendedPath prefix
        if (extendedPath.Path != path.Append(0, 64 - path.Length).Path)
        {
            ValueHash256 gapFrom = path.Append(0, 64 - path.Length).Path;
            ValueHash256 gapTo = DecrementPath(extendedPath.Append(0, 64 - extendedPath.Length).Path);
            if (gapTo.CompareTo(gapFrom) >= 0)
            {
                writeBatch.DeleteStorageRange(addressHash, gapFrom, gapTo);
                writeBatch.DeleteStorageTrieNodeRange(addressHash, path.Append(0, 64 - path.Length), ComputeTreePathForHash(gapTo, 64));
            }
        }

        // Delete from end of extension's subtree to end of current subtree
        ValueHash256 afterExtension = IncrementPath(extendedPath.Append(0xF, 64 - extendedPath.Length).Path);
        ValueHash256 endOfSubtree = path.Append(0xF, 64 - path.Length).Path;
        if (afterExtension.CompareTo(endOfSubtree) <= 0)
        {
            writeBatch.DeleteStorageRange(addressHash, afterExtension, endOfSubtree);
            writeBatch.DeleteStorageTrieNodeRange(addressHash, ComputeTreePathForHash(afterExtension, 64), path.Append(0xF, 64 - path.Length));
        }
    }

    /// <summary>
    /// Compute the range of full paths covered by a subtree rooted at childPath.
    /// </summary>
    private static (ValueHash256 fromPath, ValueHash256 toPath) ComputeSubtreeRange(in TreePath childPath)
    {
        // Lower bound: childPath padded with zeros to 64 nibbles
        ValueHash256 fromPath = childPath.Append(0, 64 - childPath.Length).Path;
        // Upper bound: childPath padded with F's to 64 nibbles
        ValueHash256 toPath = childPath.Append(0xF, 64 - childPath.Length).Path;
        return (fromPath, toPath);
    }

    /// <summary>
    /// Compute the range of tree paths covered by a subtree.
    /// </summary>
    private static (TreePath fromTreePath, TreePath toTreePath) ComputeTreePathRange(in TreePath childPath)
    {
        TreePath fromTreePath = childPath.Append(0, 64 - childPath.Length);
        TreePath toTreePath = childPath.Append(0xF, 64 - childPath.Length);
        return (fromTreePath, toTreePath);
    }

    /// <summary>
    /// Create a TreePath from a ValueHash256 with specified length.
    /// </summary>
    private static TreePath ComputeTreePathForHash(in ValueHash256 hash, int length) =>
        new(hash, length);

    /// <summary>
    /// Decrement a path by 1 (treating it as a 256-bit big-endian integer).
    /// </summary>
    private static ValueHash256 DecrementPath(in ValueHash256 path)
    {
        ValueHash256 result = path;
        Span<byte> bytes = result.BytesAsSpan;

        for (int i = 31; i >= 0; i--)
        {
            if (bytes[i] > 0)
            {
                bytes[i]--;
                return result;
            }
            bytes[i] = 0xFF;
        }

        // Underflow - return zero (shouldn't happen in practice)
        return ValueKeccak.Zero;
    }

    /// <summary>
    /// Increment a path by 1 (treating it as a 256-bit big-endian integer).
    /// </summary>
    private static ValueHash256 IncrementPath(in ValueHash256 path)
    {
        ValueHash256 result = path;
        Span<byte> bytes = result.BytesAsSpan;

        for (int i = 31; i >= 0; i--)
        {
            if (bytes[i] < 0xFF)
            {
                bytes[i]++;
                return result;
            }
            bytes[i] = 0x00;
        }

        // Overflow - return max (shouldn't happen in practice)
        result = ValueKeccak.Zero;
        result.BytesAsSpan.Fill(0xFF);
        return result;
    }

    public void Flush() => persistence.Flush();

    public ITreeSyncVerificationContext CreateVerificationContext(byte[] rootNodeData) =>
        new FlatVerificationContext(persistence, rootNodeData, logManager);

    private class FlatVerificationContext : ITreeSyncVerificationContext, IDisposable
    {
        private readonly StateTree _stateTree;
        private readonly IPersistence.IPersistenceReader _reader;
        private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

        public FlatVerificationContext(IPersistence persistence, byte[] rootNodeData, ILogManager logManager)
        {
            _reader = persistence.CreateReader();
            _stateTree = new StateTree(new FlatSyncTrieStore(_reader), logManager);
            _stateTree.RootRef = new TrieNode(NodeType.Unknown, rootNodeData);
        }

        public Account? GetAccount(Hash256 addressHash)
        {
            ReadOnlySpan<byte> bytes = _stateTree.Get(addressHash.Bytes);
            return bytes.IsEmpty ? null : _accountDecoder.Decode(bytes);
        }

        public void Dispose() => _reader.Dispose();
    }

    /// <summary>
    /// Minimal trie store for verification context using IPersistenceReader directly.
    /// </summary>
    private class FlatSyncTrieStore(IPersistence.IPersistenceReader reader) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStateRlp(path, flags);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : new FlatSyncStorageTrieStore(reader, address);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            throw new NotSupportedException("Read-only");
    }

    private class FlatSyncStorageTrieStore(IPersistence.IPersistenceReader reader, Hash256 address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStorageRlp(address, path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            throw new NotSupportedException("Read-only");
    }

    /// <summary>
    /// Minimal trie node resolver that throws on all operations.
    /// Used only for ResolveNode where the node already has RLP data, so the resolver is never actually called.
    /// </summary>
    private sealed class NullTrieNodeResolver : ITrieNodeResolver
    {
        public static readonly NullTrieNodeResolver Instance = new();
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => throw new NotSupportedException();
        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw new NotSupportedException();
        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw new NotSupportedException();
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new NotSupportedException();
        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
    }
}
