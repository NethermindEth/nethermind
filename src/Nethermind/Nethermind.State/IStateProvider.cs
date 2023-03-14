// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.State
{
    /// <summary>
    /// Represents the STATE aspect of the Ethereum, acting as a persistent and a transient state provider.
    /// </summary>
    /// <remarks>
    /// The semantics of commiting the state is as follows:
    /// 1. <see cref="Commit()"/> commits the transient state to the underlying trie but does not flush it.
    /// 2. <see cref="CommitTree"/> flushes the trie and makes it use <see cref="ITrieStore"/>
    /// to make it persistent.
    /// </remarks>
    public interface IStateProvider : IReadOnlyStateProvider, IJournal<int>
    {
        void RecalculateStateRoot();

        new Keccak StateRoot { get; set; }

        void DeleteAccount(Address address);

        void CreateAccount(Address address, in UInt256 balance);

        void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce);

        void UpdateCodeHash(Address address, Keccak codeHash, IReleaseSpec spec, bool isGenesis = false);

        void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

        void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

        void UpdateStorageRoot(Address address, Keccak storageRoot);

        void IncrementNonce(Address address);

        void DecrementNonce(Address address);

        Keccak UpdateCode(ReadOnlyMemory<byte> code);

        /* snapshots */

        /// <summary>
        /// Commits the state of the world represented by this state provider to the underlying trie.
        /// </summary>
        void Commit(IReleaseSpec releaseSpec, bool isGenesis = false);

        /// <summary>
        /// Commits the state of the world represented by this state provider to the underlying trie.
        /// </summary>
        void Commit(IReleaseSpec releaseSpec, IStateTracer? stateTracer, bool isGenesis = false);

        void Reset();

        /// <summary>
        /// Commits the underlying trie with all the changes that were applied by earlier <see cref="Commit"/> calls.
        /// </summary>
        void CommitTree(long blockNumber);

        /// <summary>
        /// For witness
        /// </summary>
        /// <param name="codeHash"></param>
        void TouchCode(Keccak codeHash);
    }
}
