// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

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
        UInt256 GetOriginal(in StorageCell storageCell);

        /// <summary>
        /// Get the persistent storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        UInt256 Get(in StorageCell storageCell);

        /// <summary>
        /// Set the provided value to persistent storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        void Set(in StorageCell storageCell, in UInt256 newValue);

        /// <summary>
        /// Get the transient storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        UInt256 GetTransientState(in StorageCell storageCell);

        /// <summary>
        /// Set the provided value to transient storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        void SetTransientState(in StorageCell storageCell, in UInt256 newValue);

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
