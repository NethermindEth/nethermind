// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
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
    IScope BeginScope(BlockHeader? baseBlock);

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
        void Commit(long blockNumber);
    }

    public interface ICodeDb
    {
        byte[]? GetCode(in ValueHash256 codeHash);

        ICodeSetter BeginCodeWrite();
    }

    public interface IStorageTree
    {
        Hash256 RootHash { get; }

        byte[] Get(in UInt256 index);

        void HintGet(in UInt256 index, byte[]? value);

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
        /// </summary>
        void Clear();
    }

    public interface ICodeSetter : IDisposable
    {
        void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code);
    }
}
