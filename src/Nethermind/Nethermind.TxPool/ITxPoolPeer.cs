// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    public interface ITxPoolPeer
    {
        public PublicKey Id { get; }
        public string Enode => string.Empty;
        void SendNewTransaction(Transaction tx) => SendNewTransactions(new[] { tx }, true);
        void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx);
    }
}
