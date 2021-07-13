//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public interface IStateProvider : IReadOnlyStateProvider
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

        void Commit(IReleaseSpec releaseSpec, bool isGenesis = false);

        void Commit(IReleaseSpec releaseSpec, IStateTracer? stateTracer, bool isGenesis = false);

        void Reset();

        void Restore(int snapshot);

        void CommitTree(long blockNumber);

        int TakeSnapshot();

        /// <summary>
        /// For witness
        /// </summary>
        /// <param name="codeHash"></param>
        void TouchCode(Keccak codeHash);

        /// <summary>
        /// pruning hack
        /// </summary>
        void CommitCode();
    }
}
