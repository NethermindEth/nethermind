//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public interface IStateProvider
    {
        void RecalculateStateRoot();
        
        Keccak StateRoot { get; set; }
        
        void CreateAccount(Address address, in UInt256 balance);

        void DeleteAccount(Address address);

        bool AccountExists(Address address);

        bool IsDeadAccount(Address address);

        Account GetAccount(Address address);
        
        UInt256 GetNonce(Address address);

        UInt256 GetBalance(Address address);
        
        Keccak GetStorageRoot(Address address);

        byte[] GetCode(Keccak codeHash);
        
        Keccak UpdateCode(byte[] code);
        
        Keccak GetCodeHash(Address address);

        void UpdateCodeHash(Address address, Keccak codeHash, IReleaseSpec spec);

        void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);
        
        void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

        void UpdateStorageRoot(Address address, Keccak storageRoot);

        void IncrementNonce(Address address);
        
        void DecrementNonce(Address address);

        /* snapshots */
        
        void Commit(IReleaseSpec releaseSpec);
        
        void Commit(IReleaseSpec releaseSpec, IStateTracer stateTracer);
        
        void Reset();

        void Restore(int snapshot);
        
        void CommitTree(long blockNumber);
        
        int TakeSnapshot();
        
        /* visitors */

        void Accept(ITreeVisitor visitor, Keccak stateRoot);
    }
}