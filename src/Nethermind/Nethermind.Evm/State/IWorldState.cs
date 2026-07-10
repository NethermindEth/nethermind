// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// Represents state that can be anchored at specific state root, snapshot, committed, reverted.
/// <see cref="BeginScope"/> must be called before any other operation, or it will throw. The returned <see cref="IDisposable"/>
/// must be disposed to close the <see cref="IWorldState"/>. Multiple block can be executed or saved within the same scope.
/// </summary>
public interface IWorldState : IJournal<Snapshot>, IReadOnlyStateProvider
{
    // For scope to create genesis.
    const BlockHeader? PreGenesis = null;

    IDisposable BeginScope(BlockHeader? baseBlock);
    Task HintBal(ReadOnlyBlockAccessList bal);
    bool IsInScope { get; }
    IWorldStateScopeProvider ScopeProvider { get; }
    new ref readonly UInt256 GetBalance(Address address);
    new ref readonly ValueHash256 GetCodeHash(Address address);
    bool HasStateForBlock(BlockHeader? baseBlock);

    /// <summary>
    /// Return the original persistent storage value from the storage cell.
    /// Span is valid until the next call on this <see cref="IWorldState"/> instance.
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell);

    /// <summary>
    /// Get the persistent storage value at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <returns>Value at cell</returns>
    ReadOnlySpan<byte> Get(in StorageCell storageCell);

    /// <summary>
    /// Reads the persistent storage value at <paramref name="storageCell"/> as a full 32-byte big-endian word.
    /// </summary>
    /// <remarks>
    /// The SLOAD counterpart to <see cref="SStore"/>: it returns the value in the form the EVM stack holds it,
    /// so neither side handles the minimal-length encoding <see cref="Get"/> exposes. Unlike <see cref="Get"/>
    /// the result does not alias backend storage, so it stays valid across later calls.
    /// </remarks>
    /// <param name="storageCell">Storage location.</param>
    /// <returns>Value at cell, zero-padded to 32 bytes.</returns>
    EvmWord SLoad(in StorageCell storageCell);

    /// <summary>
    /// Set the provided value to persistent storage at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="newValue">Value to store</param>
    void Set(in StorageCell storageCell, byte[] newValue);

    /// <summary>
    /// Applies an SSTORE to <paramref name="storageCell"/> and reports the comparisons that net gas
    /// metering needs, without exposing the stored values.
    /// </summary>
    /// <remarks>
    /// The cell is written only when <paramref name="newValue"/> differs from the current value (EIP-2200).
    /// The write precedes the caller's gas accounting, so an out-of-gas SSTORE mutates state before failing;
    /// this is safe because the write is journaled and the frame's snapshot restore undoes it, along with the
    /// block-access-list entry it produces.
    /// <para>
    /// The original value is read only when the store is not a no-op, so that witness and access-list tracing
    /// observe the same accesses as a hand-rolled Get/GetOriginal/Set sequence.
    /// </para>
    /// <para>
    /// <see cref="WorldStateExtensions.ApplySStore"/> is the reference implementation, expressed in terms of
    /// <see cref="Get"/>, <see cref="GetOriginal"/> and <see cref="Set"/>. Backends that store slots as
    /// fixed-width words may instead compare without materializing the minimal-length encoding those
    /// primitives require.
    /// </para>
    /// </remarks>
    /// <param name="storageCell">Storage location.</param>
    /// <param name="newValue">The 32-byte big-endian word to store.</param>
    /// <returns>The comparisons between the original, current and new values.</returns>
    SStoreState SStore(in StorageCell storageCell, in EvmWord newValue);

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
    void Reset(bool resetBlockChanges = true);

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

    void WarmUp(AccessList? accessList, CancellationToken cancellationToken = default);

    void WarmUp(Address address);

    /// <summary>
    /// Clear all storage at specified address
    /// </summary>
    /// <param name="address">Contract address</param>
    void ClearStorage(Address address);

    /// <summary>Only valid where no revert can follow AND the round is committed before any further
    /// writes (validation mode); build-up/revertible clearing must use <see cref="ClearStorage"/>.</summary>
    void MarkStorageDestroyed(Address address) => ClearStorage(address);

    void RecalculateStateRoot();

    void DeleteAccount(Address address);

    void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default);
    void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default);
    // used by Arbitrum
    void CreateEmptyAccountIfDeleted(Address address);

    /// <summary>
    /// Inserts the given smart contract code into the system at the specified address,
    /// associating it with a unique code hash.
    /// </summary>
    /// <param name="address">The target address where the code is to be inserted.</param>
    /// <param name="codeHash">The hash representing the code content, used for deduplication and reference.</param>
    /// <param name="code">The bytecode to be inserted.</param>
    /// <param name="spec">The current release specification which may affect validation or processing rules.</param>
    /// <param name="isGenesis">Indicates whether the insertion is part of the genesis block setup.</param>
    /// <returns>True if the code was inserted to the database at that hash; otherwise false if it was already there.
    /// Note: This is different from whether the account has its hash updated</returns>
    bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false);

    void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance);

    bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance);

    void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance);

    void IncrementNonce(Address address, ulong delta, out ulong oldNonce);

    void DecrementNonce(Address address, ulong delta);

    void DecrementNonce(Address address) => DecrementNonce(address, 1);

    void SetNonce(Address address, in ulong nonce);

    /* snapshots */
    void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true);

    /// <summary>
    /// Persist the underlying changes to the storage at the specified block number. This also recalculate state root.
    /// </summary>
    /// <param name="blockNumber"></param>
    void CommitTree(ulong blockNumber);

    void CommitTree(int blockNumber) => CommitTree((ulong)blockNumber);

    ArrayPoolList<AddressAsKey>? GetAccountChanges();

    void ResetTransient();

    public void AddAccountRead(Address address) { }

    public void RecordAccountAccess(Address address) { }

    public void RecordBytecodeAccess(Address address) { }

    public IDisposable? BeginSystemAccountReadSuppression() => null;

    // See https://eips.ethereum.org/EIPS/eip-7610
    bool IsNonZeroAccount(Address address, out bool accountExists)
    {
        accountExists = AccountExists(address);
        return accountExists
            && (IsContract(address) || !(GetNonce(address) == 0) || !IsStorageEmpty(address));
    }
}
