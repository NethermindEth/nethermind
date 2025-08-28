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
public interface IWorldStateBackend
{
    bool HasRoot(BlockHeader? baseBlock);
    IScope BeginScope(BlockHeader? baseBlock);

    public interface IScope : IDisposable
    {
        /// <summary>
        /// The top level tree that holds account information.
        /// </summary>
        IStateTree StateTree { get; }

        /// <summary>
        /// Create a per address storage tree. Multiple call to the same
        /// address yield the same <see cref="IStorageTree"/>. The returned object
        /// must not be used after <see cref="Commit"/>, and should be re-created.
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        IStorageTree CreateStorageTree(Address address);

        /// <summary>
        /// A commit will traverse the dirty nodes in the tree, calculate the hash and save
        /// the tree to underlying store.
        /// That said, <see cref="WorldState"/> will always call <see cref="IStateTree.UpdateRootHash"/>
        /// first.
        /// </summary>
        void Commit(long blockNumber);
    }

    public interface IStateTree
    {
        Hash256 RootHash { get; }
        Account? Get(Address address);

        void Set(Address key, Account account);
        void UpdateRootHash();
    }

    public interface IStorageTree
    {
        // RootHash of the storage tree.
        // May need to call `UpdateRootHash`.
        // Note: StorageRoot of the account is not set by the backend.
        Hash256 RootHash { get; }
        byte[] Get(in UInt256 index);

        /// <summary>
        /// Used by JS tracer. May not work on some database layout.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[] Get(in ValueHash256 hash);

        /// <summary>
        /// Self-destruct. Maybe costly.
        /// </summary>
        void Clear();
        void Set(in UInt256 index, byte[] value);
        void UpdateRootHash();
    }
}
