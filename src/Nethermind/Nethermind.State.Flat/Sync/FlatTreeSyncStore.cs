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
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            currentState = reader.CurrentState;
        }

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
        }
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
