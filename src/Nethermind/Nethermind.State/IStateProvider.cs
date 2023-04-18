// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.State
{
    public interface IStateProvider : IReadOnlyStateProvider
    {
        void RecalculateStateRoot();

        new Keccak StateRoot { get; set; }

        void DeleteAccount(Address address);

        void CreateAccount(Address address, in UInt256 balance);

        void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce);

        void InsertCode(Address address, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false);

        void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

        void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

        void UpdateStorageRoot(Address address, Keccak storageRoot);

        void IncrementNonce(Address address);

        void DecrementNonce(Address address);

        /* snapshots */

        void Commit(IReleaseSpec releaseSpec, bool isGenesis = false);

        void Reset();

        void CommitTree(long blockNumber);

        /// <summary>
        /// For witness
        /// </summary>
        /// <param name="codeHash"></param>
        void TouchCode(Keccak codeHash);

        void SetNonce(Address address, in UInt256 nonce);
    }
}
