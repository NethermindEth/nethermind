// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StorageProvider : IStorageProvider
    {
        readonly PersistentStorageProvider _persistentStorageProvider;
        readonly TransientStorageProvider _transientStorageProvider;

        public StorageProvider(ITrieStore trieStore, IStateProvider stateProvider, ILogManager logManager)
        {
            _persistentStorageProvider = new PersistentStorageProvider(trieStore, stateProvider, logManager);
            _transientStorageProvider = new TransientStorageProvider(logManager);
        }

        public void ClearStorage(Address address)
        {
            _persistentStorageProvider.ClearStorage(address);
            _transientStorageProvider.ClearStorage(address);
        }

        public void Commit()
        {
            _persistentStorageProvider.Commit();
            _transientStorageProvider.Commit();
        }

        public void Commit(IStorageTracer stateTracer)
        {
            _persistentStorageProvider.Commit(stateTracer);
            _transientStorageProvider.Commit(stateTracer);
        }

        public void CommitTrees(long blockNumber)
        {
            _persistentStorageProvider.CommitTrees(blockNumber);
        }

        public byte[] Get(in StorageCell storageCell)
        {
            return _persistentStorageProvider.Get(storageCell);
        }

        public byte[] GetOriginal(in StorageCell storageCell)
        {
            return _persistentStorageProvider.GetOriginal(storageCell);
        }

        public byte[] GetTransientState(in StorageCell storageCell)
        {
            return _transientStorageProvider.Get(storageCell);
        }

        public void Reset()
        {
            _persistentStorageProvider.Reset();
            _transientStorageProvider.Reset();
        }

        /// <summary>
        /// Convenience for test cases
        /// </summary>
        /// <param name="snapshot"></param>
        internal void Restore(int snapshot)
        {
            Restore(new Snapshot.Storage(snapshot, Snapshot.EmptyPosition));
        }

        public void Restore(Snapshot.Storage snapshot)
        {
            _persistentStorageProvider.Restore(snapshot.PersistentStorageSnapshot);
            _transientStorageProvider.Restore(snapshot.TransientStorageSnapshot);
        }

        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            _persistentStorageProvider.Set(storageCell, newValue);
        }

        public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        {
            _transientStorageProvider.Set(storageCell, newValue);
        }

        Snapshot.Storage IStorageProvider.TakeSnapshot(bool newTransactionStart)
        {
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);

            return new Snapshot.Storage(persistentSnapshot, transientSnapshot);
        }
    }
}
