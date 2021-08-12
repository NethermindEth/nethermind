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

namespace Nethermind.State
{
    public interface IStorageProvider
    {
        byte[] GetOriginal(StorageCell storageCell);
        
        byte[] Get(StorageCell storageCell);

        void Set(StorageCell storageCell, byte[] newValue);

        void Reset();
        
        void CommitTrees(long blockNumber);
        
        void Restore(int snapshot);

        void Commit();
        
        void Commit(IStorageTracer stateTracer);
        
        /// <summary>
        /// Creates a restartable snapshot.
        /// </summary>
        /// <param name="newTransactionStart"> Indicates new transaction will start here.</param>
        /// <returns>Snapshot index</returns>
        /// <remarks>
        /// If <see cref="newTransactionStart"/> is true and there are already changes in <see cref="IStorageProvider"/> then next call to
        /// <see cref="GetOriginal"/> will use changes before this snapshot as original values for this new transaction.
        /// </remarks>
        int TakeSnapshot(bool newTransactionStart = false);

        void ClearStorage(Address address);
    }
}
