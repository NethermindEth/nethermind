// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public interface IPooledTxsRequestor
    {
        void RequestTransactions(Action<GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashes, Guid sessionId);
        void RequestTransactionsEth66(Action<V66.Messages.GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashes, Guid sessionId);
        void RequestTransactionsEth68(Action<V66.Messages.GetPooledTransactionsMessage> send, IOwnedReadOnlyList<Hash256> hashes, IOwnedReadOnlyList<int> sizes, IOwnedReadOnlyList<byte> types, Guid sessionId);
    }
}
