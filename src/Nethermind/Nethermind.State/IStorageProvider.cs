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
    /// <summary>
    /// Interface for the StorageProvider
    /// Includes both persistent and transient storage
    /// </summary>
    public interface IStorageProvider : IJournal<Snapshot.Storage>
    {
        /// <summary>
        /// Return the original persistent storage value from the storage cell
        /// </summary>
        /// <param name="storageCell"></param>
        /// <returns></returns>
        byte[] GetOriginal(StorageCell storageCell);
        
        /// <summary>
        /// Get the persistent storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        byte[] Get(StorageCell storageCell);

        /// <summary>
        /// Set the provided value to persistent storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        void Set(StorageCell storageCell, byte[] newValue);

        /// <summary>
        /// Get the transient storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        byte[] GetTransientState(StorageCell storageCell);

        /// <summary>
        /// Set the provided value to transient storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        void SetTransientState(StorageCell storageCell, byte[] newValue);

        /// <summary>
        /// Reset all storage
        /// </summary>
        void Reset();
        
        /// <summary>
        /// Commit persisent storage trees
        /// </summary>
        /// <param name="blockNumber">Current block number</param>
        void CommitTrees(long blockNumber);
        
        /// <summary>
        /// Commit persistent storage
        /// </summary>
        void Commit();
        
        /// <summary>
        /// Commit persistent storage
        /// </summary>
        /// <param name="stateTracer">State tracer</param>
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
        Snapshot.Storage TakeSnapshot(bool newTransactionStart = false);

        Snapshot.Storage IJournal<Snapshot.Storage>.TakeSnapshot() => TakeSnapshot();

        /// <summary>
        /// Clear all storage at specified address
        /// </summary>
        /// <param name="address">Contract address</param>
        void ClearStorage(Address address);
    }
}
