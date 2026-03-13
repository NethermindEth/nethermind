// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class FlatLocalDbContext(IPersistence persistence, ILogManager logManager) : IStateSyncTestOperation
{
    public Hash256 RootHash
    {
        get
        {
            using IPersistence.IPersistenceReader reader = persistence.CreateReader();
            return reader.CurrentState.StateRoot.ToHash256();
        }
    }

    public void UpdateRootHash()
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(reader.CurrentState, reader.CurrentState);
        WritableTrieStore adapter = new(reader, writeBatch);
        StateTree tree = new(adapter, logManager);
        tree.UpdateRootHash();
        tree.Commit();
    }

    public void SetAccountsAndCommit(params (Hash256 Address, Account? Account)[] accounts)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(reader.CurrentState, reader.CurrentState);
        WritableTrieStore adapter = new(reader, writeBatch);
        StateTree tree = new(adapter, logManager);

        foreach (var (address, account) in accounts)
            tree.Set(address, account);
        tree.Commit();
    }

    public void AssertFlushed()
    {
        // For flat, sync finalization writes to persistence. Verify root node exists.
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        reader.TryLoadStateRlp(TreePath.Empty, ReadFlags.None).Should().NotBeNull("root node should exist after flush");
    }

    public void CompareTrees(RemoteDbContext remote, ILogger logger, string stage, bool skipLogs = false)
    {
        if (!skipLogs) logger.Info($"==================== {stage} ====================");

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        StateTree localTree = new(new ReadOnlyTrieStore(reader), logManager);
        localTree.RootHash = remote.StateTree.RootHash;

        if (!skipLogs) logger.Info("-------------------- REMOTE --------------------");
        TreeDumper dumper = new();
        remote.StateTree.Accept(dumper, remote.StateTree.RootHash);
        string remoteStr = dumper.ToString();
        if (!skipLogs) logger.Info(remoteStr);
        if (!skipLogs) logger.Info("-------------------- LOCAL --------------------");
        dumper.Reset();
        localTree.Accept(dumper, localTree.RootHash);
        string localStr = dumper.ToString();
        if (!skipLogs) logger.Info(localStr);

        if (stage == "END")
        {
            Assert.That(localStr, Is.EqualTo(remoteStr), $"{stage}\n{remoteStr}\n{localStr}");
        }
    }

    public void DeleteStateRoot()
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(reader.CurrentState, reader.CurrentState);
        writeBatch.DeleteStateTrieNodeRange(TreePath.Empty, TreePath.Empty);
    }

    /// <summary>
    /// Read-only trie store for reading state trie nodes from flat persistence.
    /// </summary>
    private class ReadOnlyTrieStore(IPersistence.IPersistenceReader reader) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStateRlp(path, flags);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : new ReadOnlyStorageTrieStore(reader, address);
    }

    /// <summary>
    /// Read-only trie store for reading storage trie nodes from flat persistence.
    /// </summary>
    private class ReadOnlyStorageTrieStore(IPersistence.IPersistenceReader reader, Hash256 address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStorageRlp(address, path, flags);
    }

    /// <summary>
    /// Writable trie store that writes trie nodes and flat entries to persistence.
    /// </summary>
    private class WritableTrieStore(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch writeBatch) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStateRlp(path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            new StateCommitter(writeBatch);

        public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            address is null ? this : new WritableStorageTrieStore(reader, writeBatch, address);

        private sealed class StateCommitter(IPersistence.IWriteBatch writeBatch) : ICommitter
        {
            public TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                writeBatch.SetStateTrieNode(path, node);
                FlatEntryWriter.WriteAccountFlatEntries(writeBatch, path, node);
                return node;
            }

            public void Dispose() { }
            public bool TryRequestConcurrentQuota() => false;
            public void ReturnConcurrencyQuota() { }
        }
    }

    /// <summary>
    /// Writable storage trie store that writes trie nodes and flat entries to persistence.
    /// </summary>
    private class WritableStorageTrieStore(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch writeBatch,
        Hash256 address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStorageRlp(address, path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            new StorageCommitter(writeBatch, address);

        private sealed class StorageCommitter(IPersistence.IWriteBatch writeBatch, Hash256 address) : ICommitter
        {
            public TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                writeBatch.SetStorageTrieNode(address, path, node);
                FlatEntryWriter.WriteStorageFlatEntries(writeBatch, address, path, node);
                return node;
            }

            public void Dispose() { }
        }
    }
}
