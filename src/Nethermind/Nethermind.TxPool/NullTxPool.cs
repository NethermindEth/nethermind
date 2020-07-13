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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.TxPool
{
    public interface ITxPoolPeer
    {
        public PublicKey Id { get; }
        void SendNewTransaction(Transaction tx, bool isPriority);
    }
    
    public class NullTxPool : ITxPool
    {
        private NullTxPool()
        {
        }

        public static NullTxPool Instance { get; } = new NullTxPool();

        public Transaction[] GetPendingTransactions() => Array.Empty<Transaction>();
        
        public Transaction[] GetOwnPendingTransactions() => Array.Empty<Transaction>();

        public void AddPeer(ITxPoolPeer peer)
        {
        }

        public void RemovePeer(PublicKey nodeId)
        {
        }

        public AddTxResult AddTransaction(Transaction tx, TxHandlingOptions txHandlingOptions) => AddTxResult.Added;

        public void RemoveTransaction(Keccak hash, long blockNumber)
        {
        }

        public bool TryGetPendingTransaction(Keccak hash, out Transaction transaction)
        {
            transaction = null;
            return false;
        }
        
        public UInt256 ReserveOwnTransactionNonce(Address address) => UInt256.Zero;

        public event EventHandler<TxEventArgs> NewPending
        {
            add { }
            remove { }
        }

        public event EventHandler<TxEventArgs> RemovedPending
        {
            add { }
            remove { }
        }
    }
}