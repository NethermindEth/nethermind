// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    /// <summary>
    /// Represents the STORAGE aspect of Ethereum, acting as a persistent and a transient storage provider.
    /// </summary>
    /// <remarks>
    /// The semantics of commiting the storage is as follows:
    /// 1. <see cref="Commit()"/> commits the transient changes to underlying tries but does not flush them.
    /// 2. <see cref="CommitTrees"/> flushes underlying tries and makes them use <see cref="ITrieStore"/>
    /// to make them persistent.
    /// </remarks>
    public interface IStorageProvider : IJournal<Snapshot.Storage>
    {
        /// <summary>
        /// Return the original persistent storage value from the storage cell
        /// </summary>
        /// <param name="storageCell"></param>
        /// <returns></returns>
        byte[] GetOriginal(in StorageCell storageCell);

        /// <summary>
        /// Get the persistent storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        byte[] Get(in StorageCell storageCell);

        /// <summary>
        /// Set the provided value to persistent storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        void Set(in StorageCell storageCell, byte[] newValue);

        /// <summary>
        /// Get the transient storage value at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at cell</returns>
        byte[] GetTransientState(in StorageCell storageCell);

        /// <summary>
        /// Set the provided value to transient storage at the specified storage cell
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <param name="newValue">Value to store</param>
        void SetTransientState(in StorageCell storageCell, byte[] newValue);

        /// <summary>
        /// Reset all storage
        /// </summary>
        void Reset();

        /// <summary>
        /// Commits underlying tries with all the changes that were applied by earlier <see cref="Commit"/> calls.
        /// </summary>
        void CommitTrees(long blockNumber);

        /// <summary>
        /// Commits the storage represented by this storage provider to underlying tries.
        /// </summary>
        void Commit();

        /// <summary>
        /// Commits the storage represented by this storage provider to underlying tries.
        /// </summary>
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
