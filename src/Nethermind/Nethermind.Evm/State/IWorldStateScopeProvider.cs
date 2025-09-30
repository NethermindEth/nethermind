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

        Account? Get(Address address);

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
        /// <param name="onAccountUpdated">Called when a storage root for an account is updated</param>
        /// <returns></returns>
        IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum, Action<Address, Account> onAccountUpdated);

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
        bool WasEmptyTree { get; }

        byte[] Get(in UInt256 index);

        /// <summary>
        /// Used by JS tracer. May not work on some database layout.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[] Get(in ValueHash256 hash);
    }

    public interface IWorldStateWriteBatch : IDisposable
    {
        void Set(Address key, Account? account);
        IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries);
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
