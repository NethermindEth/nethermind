// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State.Tracing;

namespace Nethermind.State;
/// <summary>
/// Represents state that can be anchored at specific state root, snapshot, committed, reverted.
/// Current format is an intermittent form on the way to a better state management.
/// </summary>
public interface IWorldState : IJournal<Snapshot>, IReadOnlyStateProvider
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
    ReadOnlySpan<byte> Get(in StorageCell storageCell);

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
    ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell);

    /// <summary>
    /// Set the provided value to transient storage at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="newValue">Value to store</param>
    void SetTransientState(in StorageCell storageCell, byte[] newValue);

    /// <summary>
    /// Reset all storage
    /// </summary>
    void Reset(bool resizeCollections = false);

    /// <summary>
    /// Creates a restartable snapshot.
    /// </summary>
    /// <param name="newTransactionStart"> Indicates new transaction will start here.</param>
    /// <returns>Snapshot index</returns>
    /// <remarks>
    /// If <see cref="newTransactionStart"/> is true and there are already changes in <see cref="IStorageProvider"/> then next call to
    /// <see cref="GetOriginal"/> will use changes before this snapshot as original values for this new transaction.
    /// </remarks>
    Snapshot TakeSnapshot(bool newTransactionStart = false);

    Snapshot IJournal<Snapshot>.TakeSnapshot() => TakeSnapshot();
    void WarmUp(AccessList? accessList);
    void WarmUp(Address address);
    /// <summary>
    /// Clear all storage at specified address
    /// </summary>
    /// <param name="address">Contract address</param>
    void ClearStorage(Address address);

    void RecalculateStateRoot();

    new Hash256 StateRoot { get; set; }

    void DeleteAccount(Address address);

    void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default);
    void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default);

    void InsertCode(Address address, Hash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false);

    void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

    void AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec);

    void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

    void UpdateStorageRoot(Address address, Hash256 storageRoot);

    void IncrementNonce(Address address, UInt256 delta);

    void DecrementNonce(Address address, UInt256 delta);

    void IncrementNonce(Address address) => IncrementNonce(address, UInt256.One);

    void DecrementNonce(Address address) => DecrementNonce(address, UInt256.One);

    /* snapshots */

    void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitStorageRoots = true);

    void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? tracer, bool isGenesis = false, bool commitStorageRoots = true);

    void CommitTree(long blockNumber);
    ArrayPoolList<AddressAsKey>? GetAccountChanges();

    bool ClearCache() => false;
}
