// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapTrieFactory(
    INodeStorage nodeStorage,
    [KeyFilter(DbNames.State)] IDb stateDb,
    ILogManager logManager) : ISnapTrieFactory
{
    private static readonly byte[] RangePhaseKey = "AccountProgressKey"u8.ToArray();

    private readonly RawScopedTrieStore _stateTrieStore = new(nodeStorage, null);

    public bool IsRangePhaseFinished()
    {
        byte[]? recorded = stateDb.Get(RangePhaseKey);
        return recorded is { Length: 32 } && new ValueHash256(recorded) == ValueKeccak.MaxValue;
    }

    public void MarkRangePhaseFinished()
    {
        stateDb.PutSpan(RangePhaseKey, ValueKeccak.MaxValue.Bytes, WriteFlags.DisableWAL);
        stateDb.Flush();
    }

    public ISnapTree<PathWithAccount> CreateStateTree()
    {
        SnapUpperBoundAdapter adapter = new(_stateTrieStore);
        return new PatriciaSnapStateTree(new StateTree(adapter, logManager), adapter, nodeStorage);
    }

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath)
    {
        Hash256 address = accountPath.ToCommitment();
        INodeStorage.IAtomicNodeStorageBatch batch = new LazyAtomicWriteBatch(nodeStorage);
        try
        {
            RawScopedTrieStore storageTrieStore = new(nodeStorage, address, batch);
            SnapUpperBoundAdapter adapter = new(storageTrieStore);
            return new PatriciaSnapStorageTree(new StorageTree(adapter, logManager), adapter, nodeStorage, address, batch);
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    private sealed class LazyAtomicWriteBatch(INodeStorage storage) : INodeStorage.IAtomicNodeStorageBatch
    {
        private INodeStorage.IAtomicNodeStorageBatch? _inner;
        private bool _disposed;

        private INodeStorage.IAtomicNodeStorageBatch Inner
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _inner ??= storage.StartAtomicWriteBatch();
            }
        }

        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags flags) =>
            Inner.Set(address, path, hash, data, flags);

        public void Commit()
        {
            try
            {
                Inner.Commit();
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner?.Dispose();
        }
    }
}
