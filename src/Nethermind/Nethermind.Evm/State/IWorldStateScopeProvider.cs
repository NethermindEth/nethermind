// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// An interface for storage backend for <see cref="WorldState"/>. This interface does not have
/// logic for snapshot/rollback and code. Operations must be done using <see cref="IScope"/>.
/// </summary>
public interface IWorldStateScopeProvider
{
    bool HasRoot(BlockHeader? baseBlock);

    /// <param name="metrics">
    /// Per-scope accumulator the world state folds into the global counters at commit/scope end. Scopes
    /// that record state/storage access metrics (e.g. the prewarmer) increment it; others ignore it.
    /// </param>
    IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics);

    public interface IScope : IDisposable
    {
        Hash256 RootHash { get; }

        void UpdateRootHash();

        /// <summary>
        /// Get the account information for the following address.
        /// Note: Do not rely on <see cref="Account.StorageRoot"/> as it may be modified after write. Instead use <see cref="IStorageTree.RootHash"/>.
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        Account? Get(Address address);

        /// <summary>
        /// Call when top level application read an account without going through this scope to reduce time during commit later.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="account"></param>
        void HintGet(Address address, Account? account);

        /// <summary>
        /// The code db
        /// </summary>
        ICodeDb CodeDb { get; }

        /// <summary>
        /// Create a per address storage tree. Multiple call to the same
        /// address yield the same <see cref="IStorageTree"/>. The returned object
        /// must not be used after <see cref="Commit"/>, and should be re-created.
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        IStorageTree CreateStorageTree(Address address);

        /// <summary>
        /// Begin a write batch to update the world state.
        /// </summary>
        /// <param name="estimatedAccountNum">For optimization, estimated account number.</param>
        /// <returns></returns>
        IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum);

        /// <summary>
        /// A commit will traverse the dirty nodes in the tree, calculate the hash and save
        /// the tree to underlying store.
        /// That said, <see cref="WorldState"/> will always call <see cref="IStateTree.UpdateRootHash"/>
        /// first.
        /// </summary>
        void Commit(ulong blockNumber);

        /// <summary>
        /// Hint that the given Block Access List will be accessed during block execution.
        /// Walks the BAL in parallel and, per account, enqueues trie-warmer jobs for the
        /// addresses + changed storage slots. When <paramref name="sink"/> is supplied, the
        /// same pass also reads each account and slot value (changed + read-only) and forwards
        /// them to the sink — letting the prewarmer populate its caches without paying a
        /// second <c>GetAccount</c> per entry.
        /// Non-prewarmer scopes may ignore the hint or treat it as a no-op.
        /// </summary>
        /// <param name="bal">The Block Access List describing addresses and storage slots to prefetch.</param>
        /// <param name="sink">Optional sink that receives each account/slot value read during the pass.</param>
        /// <param name="priorityTransactions">Optional block transaction order used by implementations to prioritize best-effort warmup.</param>
        /// <returns>A task that completes when the asynchronous warmup finishes.</returns>
        Task HintBal(ReadOnlyBlockAccessList bal, IAsyncBalReaderSink? sink = null, Transaction[]? priorityTransactions = null);
    }

    /// <summary>
    /// Sink that receives account and storage values read from the underlying
    /// trie/db during a BAL (Block Access List) scan.
    /// </summary>
    public interface IAsyncBalReaderSink
    {
        /// <summary>
        /// Called when an account has been read for the given address.
        /// </summary>
        /// <param name="address">The account address from the BAL.</param>
        /// <param name="account">The account data, or null if the account does not exist.</param>
        void OnAccountRead(Address address, Account? account);

        /// <summary>
        /// Called when a storage slot has been read for the given cell.
        /// </summary>
        /// <param name="storageCell">The storage cell (address + slot index).</param>
        /// <param name="value">The storage value bytes.</param>
        void OnStorageRead(in StorageCell storageCell, byte[] value);

        /// <summary>
        /// Returns whether the BAL reader should still fetch the given account.
        /// Implementations backed by a cache can return <c>false</c> and hand back the
        /// cached <paramref name="account"/> to let the reader skip the fetch.
        /// </summary>
        /// <param name="address">The account address from the BAL.</param>
        /// <param name="account">The cached account, or null when not cached.</param>
        /// <returns><c>true</c> if the reader should fetch; <c>false</c> if it should skip.</returns>
        bool StillNeeded(Address address, out Account? account)
        {
            account = null;
            return true;
        }

        /// <summary>
        /// Returns whether the BAL reader should still fetch the given storage cell.
        /// Implementations backed by a cache can return <c>false</c> to let the reader skip the fetch.
        /// </summary>
        /// <param name="storageCell">The storage cell from the BAL.</param>
        /// <returns><c>true</c> if the reader should fetch; <c>false</c> if it should skip.</returns>
        bool StillNeeded(in StorageCell storageCell) => true;
    }

    public interface ICodeDb
    {
        byte[]? GetCode(in ValueHash256 codeHash);

        ICodeSetter BeginCodeWrite();

        /// <summary>
        /// Hint: returns true only if this code is durably persisted in this codeDb.
        /// Used by callers to avoid redundant inserts. Implementations must never produce
        /// false positives — a true return means the code is retrievable via <see cref="GetCode"/>.
        /// Default returns false (safe overlay behaviour: never claim something is persisted).
        /// </summary>
        bool ContainsCode(in ValueHash256 codeHash) => false;

        /// <summary>
        /// Hint: the code with the given hash was just persisted to this codeDb. Permits
        /// the implementation to update an internal cache used by <see cref="ContainsCode"/>.
        /// Default is a no-op (safe overlay behaviour).
        /// </summary>
        void MarkCodePersisted(in ValueHash256 codeHash) { }
    }

    public interface IStorageTree
    {
        Hash256 RootHash { get; }

        byte[] Get(in UInt256 index);

        /// <summary>
        /// Hint that a slot is being written. Backends may use this to start asynchronous
        /// trie warm-up for the slot path.
        /// </summary>
        void HintSet(in UInt256 index, byte[]? value);

        /// <summary>
        /// Used by JS tracer. May not work on some database layout.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[] Get(in ValueHash256 hash);
    }

    public interface IWorldStateWriteBatch : IDisposable
    {
        public event EventHandler<AccountUpdated> OnAccountUpdated;

        // Note: Null account imply removal and clearing of storage.
        void Set(Address key, Account? account);

        IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries);
    }

    public class AccountUpdated(Address Address, Account? Account) : EventArgs
    {
        public Address Address { get; init; } = Address;
        public Account? Account { get; init; } = Account;

        public void Deconstruct(out Address Address, out Account? Account)
        {
            Address = this.Address;
            Account = this.Account;
        }
    }

    public interface IStorageWriteBatch : IDisposable
    {
        void Set(in UInt256 index, byte[] value);

        /// <summary>
        /// Self-destruct. Maybe costly. Must be called first.
        /// Note: Is called on new account or dead account at start of tx also.
        /// Note: May not get called if the account is removed at the end of the time of commit.
        /// </summary>
        void Clear();
    }

    public interface ICodeSetter : IDisposable
    {
        void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code);
    }
}
