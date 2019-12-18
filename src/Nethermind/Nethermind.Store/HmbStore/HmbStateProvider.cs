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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Store.HmbStore
{
    public class HmbStateProvider : IStateProvider, INodeDataConsumer
    {
        private readonly ILogger _logger;

        public HmbStateProvider(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<HmbStateProvider>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public Keccak StateRoot { get; set; }
        public void DeleteAccount(Address address)
        {
            throw new System.NotImplementedException();
        }

        public void CreateAccount(Address address, in UInt256 balance)
        {
            throw new System.NotImplementedException();
        }

        public bool AccountExists(Address address)
        {
            throw new System.NotImplementedException();
        }

        public bool IsDeadAccount(Address address)
        {
            throw new System.NotImplementedException();
        }

        public bool IsEmptyAccount(Address address)
        {
            throw new System.NotImplementedException();
        }

        public Account GetAccount(Address address)
        {
            throw new System.NotImplementedException();
        }

        public UInt256 GetNonce(Address address)
        {
            throw new System.NotImplementedException();
        }

        public UInt256 GetBalance(Address address)
        {
            throw new System.NotImplementedException();
        }

        public Keccak GetStorageRoot(Address address)
        {
            throw new System.NotImplementedException();
        }

        public Keccak GetCodeHash(Address address)
        {
            throw new System.NotImplementedException();
        }

        public byte[] GetCode(Address address)
        {
            throw new System.NotImplementedException();
        }

        public byte[] GetCode(Keccak codeHash)
        {
            throw new System.NotImplementedException();
        }

        public void UpdateCodeHash(Address address, Keccak codeHash, IReleaseSpec spec)
        {
            throw new System.NotImplementedException();
        }

        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            throw new System.NotImplementedException();
        }

        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            throw new System.NotImplementedException();
        }

        public void UpdateStorageRoot(Address address, Keccak storageRoot)
        {
            throw new System.NotImplementedException();
        }

        public void IncrementNonce(Address address)
        {
            throw new System.NotImplementedException();
        }

        public Keccak UpdateCode(byte[] code)
        {
            throw new System.NotImplementedException();
        }

        public void Reset()
        {
            throw new System.NotImplementedException();
        }

        public void CommitTree()
        {
            throw new System.NotImplementedException();
        }

        public void Restore(int snapshot)
        {
            throw new System.NotImplementedException();
        }

        public void Commit(IReleaseSpec releaseSpec)
        {
            throw new System.NotImplementedException();
        }

        public void Commit(IReleaseSpec releaseSpec, IStateTracer stateTracer)
        {
            throw new System.NotImplementedException();
        }

        public int TakeSnapshot()
        {
            throw new System.NotImplementedException();
        }

        public string DumpState()
        {
            throw new System.NotImplementedException();
        }

        public TrieStats CollectStats()
        {
            throw new System.NotImplementedException();
        }

        public void DecrementNonce(Address address)
        {
            throw new System.NotImplementedException();
        }

        public event EventHandler NeedMoreData;
        
        public Keccak[] PrepareRequest()
        {
            throw new NotImplementedException();
        }
    }
}