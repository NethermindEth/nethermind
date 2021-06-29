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
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public class NullTxPool : ITxPool
    {
        private NullTxPool() { }

        public static NullTxPool Instance { get; } = new();

        public int GetPendingTransactionsCount() => 0;

        public Transaction[] GetPendingTransactions() => Array.Empty<Transaction>();
        
        public Transaction[] GetOwnPendingTransactions() => Array.Empty<Transaction>();
        
        public IDictionary<Address, Transaction[]> GetPendingTransactionsBySender() => new Dictionary<Address, Transaction[]>();

        public void AddPeer(ITxPoolPeer peer) { }

        public void RemovePeer(PublicKey nodeId) { }
        
        public AddTxResult SubmitTx(Transaction tx, TxHandlingOptions txHandlingOptions) => AddTxResult.Added;

        public bool RemoveTransaction(Keccak? hash) => false;
        
        public bool IsKnown(Keccak hash) => false;

        public bool TryGetPendingTransaction(Keccak hash, out Transaction? transaction)
        {
            transaction = null;
            return false;
        }

        public UInt256 ReserveOwnTransactionNonce(Address address) => UInt256.Zero;

        public event EventHandler<TxEventArgs> NewDiscovered
        {
            add { }
            remove { }
        }

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
