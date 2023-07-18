// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public interface IPooledTxsRequestor
    {
        void RequestTransactions(Action<GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes);
        void RequestTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes);
        void RequestTransactionsEth68(Action<V66.Messages.GetPooledTransactionsMessage> send, IReadOnlyList<Keccak> hashes, IReadOnlyList<int> sizes);
    }
}
