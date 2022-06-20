//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StorageProvider : IStorageProvider
    {
        PersistentStorageProvider _persistentStorageProvider;
        TransientStorageProvider _transientStorageProvider;

        public StorageProvider(ITrieStore? trieStore, IStateProvider? stateProvider, ILogManager? logManager)
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

        public byte[] Get(StorageCell storageCell)
        {
            return _persistentStorageProvider.Get(storageCell);
        }

        public byte[] GetOriginal(StorageCell storageCell)
        {
            return _persistentStorageProvider.GetOriginal(storageCell);
        }

        public byte[] GetTransientState(StorageCell storageCell)
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
        public void Restore(int snapshot)
        {
            Restore(new Snapshot(Snapshot.EmptyPosition, snapshot, Snapshot.EmptyPosition));
        }
        public void Restore(Snapshot snapshot)
        {
            _persistentStorageProvider.Restore(snapshot.PersistentStorageSnapshot);
            _transientStorageProvider.Restore(snapshot.TransientStorageSnapshot);
        }

        public void Set(StorageCell storageCell, byte[] newValue)
        {
            _persistentStorageProvider.Set(storageCell, newValue);
        }

        public void SetTransientState(StorageCell storageCell, byte[] newValue)
        {
            _transientStorageProvider.Set(storageCell, newValue);
        }

        Snapshot IStorageProvider.TakeSnapshot(bool newTransactionStart)
        {
            int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
            int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);

            return new Snapshot(Snapshot.EmptyPosition, persistentSnapshot, transientSnapshot);
        }
    }
}
