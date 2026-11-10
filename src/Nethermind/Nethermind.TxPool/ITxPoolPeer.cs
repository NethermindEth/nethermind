// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    public interface ITxPoolPeer
    {
        public PublicKey Id { get; }
        public string Enode => string.Empty;
        ulong HeadNumber { get; set; }
        void SendNewTransaction(Transaction tx) => SendNewTransactions(new ReadOnlySpan<Transaction>(in tx), true);

        /// <summary>Notifies the peer about <paramref name="txs"/>, as full transactions or as hash announcements.</summary>
        /// <remarks>The span is valid only for the duration of the call; an implementation copies what it keeps.</remarks>
        void SendNewTransactions(ReadOnlySpan<Transaction> txs, bool sendFullTx);
    }
}
