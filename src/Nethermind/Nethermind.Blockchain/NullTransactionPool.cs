/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain
{
    public class NullTransactionPool : ITransactionPool
    {
        private NullTransactionPool()
        {
        }

        public static NullTransactionPool Instance { get; } = new NullTransactionPool();

        public Transaction[] GetPendingTransactions() => Array.Empty<Transaction>();

        public void AddFilter<T>(T filter) where T : ITransactionFilter
        {
        }

        public void AddPeer(ISynchronizationPeer peer)
        {
        }

        public void RemovePeer(NodeId nodeId)
        {
        }

        public void AddTransaction(Transaction transaction, UInt256 blockNumber)
        {
        }

        public void RemoveTransaction(Keccak hash)
        {
        }

        public event EventHandler<TransactionEventArgs> NewPending;
    }
}