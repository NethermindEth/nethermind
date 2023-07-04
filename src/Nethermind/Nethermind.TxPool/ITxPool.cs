// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool
{
    public interface ITxPool
    {
        int GetPendingTransactionsCount();
        Transaction[] GetPendingTransactions();

        /// <summary>
        /// Grouped by sender address, sorted by nonce and later tx pool sorting
        /// </summary>
        /// <returns></returns>
        IDictionary<Address, Transaction[]> GetPendingTransactionsBySender();

        /// <summary>
        /// from a specific sender, sorted by nonce and later tx pool sorting
        /// </summary>
        /// <returns></returns>
        Transaction[] GetPendingTransactionsBySender(Address address);
        void AddPeer(ITxPoolPeer peer);
        void RemovePeer(PublicKey nodeId);
        AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions handlingOptions);
        bool RemoveTransaction(Keccak? hash);
        bool IsKnown(Keccak hash);
        bool TryGetPendingTransaction(Keccak hash, out Transaction? transaction);
        UInt256 GetLatestPendingNonce(Address address);
        event EventHandler<TxEventArgs> NewDiscovered;
        event EventHandler<TxEventArgs> NewPending;
        event EventHandler<TxEventArgs> RemovedPending;
        event EventHandler<TxEventArgs> EvictedPending;
    }
}
