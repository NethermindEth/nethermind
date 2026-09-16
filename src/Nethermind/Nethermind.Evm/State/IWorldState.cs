// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Attempts to open the state committed at <paramref name="baseBlock"/> (pre-genesis when <c>null</c>).</summary>
    /// <param name="scopeCloser">The disposable scope closer when acquisition succeeds.</param>
    /// <returns><c>true</c> when the state was acquired; <c>false</c> when it is unavailable.</returns>
    bool TryBeginScope(BlockHeader? baseBlock, [NotNullWhen(true)] out IDisposable? scopeCloser);

    /// <summary>
    /// Attempts to open the state required to execute <paramref name="targetBlock"/>.
    /// </summary>
    /// <param name="targetBlock">The target block; its parent state is opened.</param>
    /// <param name="scopeCloser">The disposable scope closer when acquisition succeeds.</param>
    /// <returns><c>true</c> when the parent state was acquired; otherwise <c>false</c>.</returns>
    bool TryBeginScopeAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IDisposable? scopeCloser) => throw new NotSupportedException();

    /// <summary>Checks whether the parent state required to execute <paramref name="targetBlock"/> is available.</summary>
    /// <remarks>This check is advisory and does not reserve or pin state.</remarks>
    bool HasStateForTarget(BlockHeader targetBlock) => throw new NotSupportedException();

    Task HintBal(ReadOnlyBlockAccessList bal);
    bool IsInScope { get; }
    IWorldStateScopeProvider ScopeProvider { get; }
    new ref readonly UInt256 GetBalance(Address address);
    new ref readonly ValueHash256 GetCodeHash(Address address);
    bool HasStateForBlock(BlockHeader? baseBlock);

    /// <summary>
    /// Return the original persistent storage value from the storage cell.
    /// </summary>
    /// <param name="storageCell">Storage location.</param>
    /// <param name="value">Original value at the cell.</param>
    void GetOriginal(in StorageCell storageCell, out UInt256 value);

    /// <summary>
    /// Get the persistent storage value at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="value">Value at cell</param>
    void Get(in StorageCell storageCell, out UInt256 value);

    /// <summary>
    /// Set the provided value to persistent storage at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="newValue">Value to store</param>
    void Set(in StorageCell storageCell, in UInt256 newValue);

    /// <summary>Sets a storage value using the caller's current value for change recording.</summary>
    /// <remarks>The cell must not have changed since the caller read <paramref name="currentValue"/>.</remarks>
    void Set(in StorageCell storageCell, in UInt256 newValue, in UInt256 currentValue)
        => Set(in storageCell, in newValue);

    /// <summary>
    /// Get the transient storage value at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="value">Value at cell</param>
    void GetTransientState(in StorageCell storageCell, out UInt256 value);

    /// <summary>
    /// Set the provided value to transient storage at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="newValue">Value to store</param>
    void SetTransientState(in StorageCell storageCell, in UInt256 newValue);

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

    // EIP-684: a creation collision occurs when the destination has code or a non-zero nonce.
    bool IsNonZeroAccount(Address address, out bool accountExists)
    {
        accountExists = AccountExists(address);
        return accountExists
            && (IsContract(address) || GetNonce(address) != 0);
    }
}
