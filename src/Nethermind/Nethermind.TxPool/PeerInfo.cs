// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
