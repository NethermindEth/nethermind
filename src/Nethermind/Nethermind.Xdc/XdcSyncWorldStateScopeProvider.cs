// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.Xdc;

/// <summary>
/// Selects the normal world-state scope when its state is finalized and otherwise reads a completed XDC sync root.
/// </summary>
internal sealed class XdcSyncWorldStateScopeProvider(
    IWorldStateScopeProvider normalProvider,
    IPersistence persistence,
    IDb codeDb,
    ILogManager logManager) : IWorldStateScopeProvider, IDisposable
{
    private int _disposed;

    public bool HasRoot(BlockHeader? baseBlock) =>
        normalProvider.HasRoot(baseBlock) ||
        baseBlock?.StateRoot is { } stateRoot && HasSyncRoot(stateRoot);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        if (normalProvider.HasRoot(baseBlock))
            return normalProvider.BeginScope(baseBlock, metrics);

        if (baseBlock?.StateRoot is not { } stateRoot)
            throw new InvalidOperationException("An XDC sync scope requires a state root.");

        IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        ReadOnlyDb? readOnlyCodeDb = null;
        try
        {
            XdcSyncNodeStorage nodeStorage = new(reader);
            if (!nodeStorage.HasRoot(stateRoot))
            {
                throw new MissingTrieNodeException(
                    $"The completed XDC sync state does not contain root {stateRoot}.",
                    null,
                    TreePath.Empty,
                    stateRoot);
            }

            RawTrieStore trieStore = new(nodeStorage);
            readOnlyCodeDb = new(codeDb, createInMemWriteStore: false);
            TrieStoreScopeProvider scopeProvider = new(trieStore, readOnlyCodeDb, logManager, codeDbIsPersistent: false);
            IWorldStateScopeProvider.IScope scope = scopeProvider.BeginScope(baseBlock, metrics);
            return new OwnedSyncScope(scope, reader, readOnlyCodeDb!);
        }
        catch
        {
            try
            {
                readOnlyCodeDb?.Dispose();
            }
            finally
            {
                reader.Dispose();
            }
            throw;
        }
    }

    private bool HasSyncRoot(Hash256 stateRoot)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        return new XdcSyncNodeStorage(reader).HasRoot(stateRoot);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        (normalProvider as IDisposable)?.Dispose();
    }

    private sealed class OwnedSyncScope(
        IWorldStateScopeProvider.IScope inner,
        IPersistence.IPersistenceReader reader,
        ReadOnlyDb readOnlyCodeDb) : IWorldStateScopeProvider.IScope
    {
        private int _disposed;

        public Hash256 RootHash => inner.RootHash;

        public void UpdateRootHash() => inner.UpdateRootHash();

        public void HintWarmAccount(in ValueAddress address) => inner.HintWarmAccount(in address);

        public void HintWarmSlot(in ValueAddress address, in UInt256 index) => inner.HintWarmSlot(in address, in index);

        public Account? Get(Address address) => inner.Get(address);

        public void HintGet(Address address, Account? account) => inner.HintGet(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => inner.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => inner.CreateStorageTree(address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => inner.StartWriteBatch(estimatedAccountNum);

        public void Commit(ulong blockNumber) => inner.Commit(blockNumber);

        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => inner.WriteBackCommittedState(takeSnapshot);

        public System.Threading.Tasks.Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) =>
            inner.HintBal(bal, sink);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                inner.Dispose();
            }
            finally
            {
                try
                {
                    readOnlyCodeDb.Dispose();
                }
                finally
                {
                    reader.Dispose();
                }
            }
        }
    }
}
