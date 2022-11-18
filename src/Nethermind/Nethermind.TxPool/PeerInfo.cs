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
// 

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    internal class PeerInfo : ITxPoolPeer
    {
        private ITxPoolPeer Peer { get; }

        private LruKeyCache<Keccak> NotifiedTransactions { get; } = new(MemoryAllowance.MemPoolSize, "notifiedTransactions");

        public PeerInfo(ITxPoolPeer peer)
        {
            Peer = peer;
        }

        public PublicKey Id => Peer.Id;

        public void SendNewTransaction(Transaction tx)
        {
            Peer.SendNewTransaction(tx);
        }

        public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            Peer.SendNewTransactions(GetTxsToSendAndMarkAsNotified(txs, sendFullTx), sendFullTx);
        }

        private IEnumerable<Transaction> GetTxsToSendAndMarkAsNotified(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            foreach (Transaction tx in txs)
            {
                if (sendFullTx || (tx.Hash != null && NotifiedTransactions.Set(tx.Hash)))
                {
                    yield return tx;
                }
            }
        }

        public override string ToString() => Peer.Enode;
    }
}
