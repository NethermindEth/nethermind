// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class PooledTransactionsMessageSerializer(ITxPoolConfig? txPoolConfig = null, ISpecProvider? specProvider = null)
        : Eth66MessageSerializer<PooledTransactionsMessage, V65.Messages.PooledTransactionsMessage>(new V65.Messages.PooledTransactionsMessageSerializer(txPoolConfig, specProvider));
}
