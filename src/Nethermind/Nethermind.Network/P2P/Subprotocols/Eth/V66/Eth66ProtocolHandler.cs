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

using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66
{
    /// <summary>
    /// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
    /// </summary>
    public class Eth66ProtocolHandler : Eth65ProtocolHandler
    {
        public Eth66ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            IPooledTxsRequestor pooledTxsRequestor,
            ISpecProvider specProvider,
            ILogManager logManager)
            : base(session, serializer, nodeStatsManager, syncServer, txPool, pooledTxsRequestor, specProvider, logManager)
        {
        }
        
        public override string Name => "eth66";

        public override byte ProtocolVersion => 66;
        
        public override void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case Eth66MessageCode.GetBlockHeaders:
                    GetBlockHeadersMessage getBlockHeadersMessage
                        = Deserialize<GetBlockHeadersMessage>(message.Content);
                    ReportIn(getBlockHeadersMessage);
                    Handle(getBlockHeadersMessage);
                    break;
                case Eth66MessageCode.BlockHeaders:
                    BlockHeadersMessage headersMsg = Deserialize<BlockHeadersMessage>(message.Content);
                    ReportIn(headersMsg);
                    Handle(headersMsg.EthMessage, size);
                    break;
                case Eth66MessageCode.GetBlockBodies:
                    GetBlockBodiesMessage getBodiesMsg = Deserialize<GetBlockBodiesMessage>(message.Content);
                    ReportIn(getBodiesMsg);
                    Handle(getBodiesMsg);
                    break;
                case Eth66MessageCode.BlockBodies:
                    BlockBodiesMessage bodiesMsg = Deserialize<BlockBodiesMessage>(message.Content);
                    ReportIn(bodiesMsg);
                    HandleBodies(bodiesMsg.EthMessage, size);
                    break;
                case Eth66MessageCode.PooledTransactions:
                    PooledTransactionsMessage pooledTxMsg
                        = Deserialize<PooledTransactionsMessage>(message.Content);
                    Metrics.Eth65PooledTransactionsReceived++;
                    ReportIn(pooledTxMsg);
                    Handle(pooledTxMsg.EthMessage);
                    break;
                case Eth66MessageCode.GetPooledTransactions:
                    GetPooledTransactionsMessage getPooledTxMsg
                        = Deserialize<GetPooledTransactionsMessage>(message.Content);
                    ReportIn(getPooledTxMsg);
                    Handle(getPooledTxMsg);
                    break;
            }
            base.HandleMessage(message);
        }
        
        public void Handle(GetBlockHeadersMessage getBlockHeaders)
        {
            Eth.V62.BlockHeadersMessage ethBlockHeadersMessage = FulfillBlockHeadersRequest(getBlockHeaders.EthMessage);
            Send(new BlockHeadersMessage(getBlockHeaders.RequestId, ethBlockHeadersMessage, int.MaxValue));
        }

        public void Handle(GetBlockBodiesMessage getBlockBodies)
        {
            Eth.V62.BlockBodiesMessage ethBlockBodiesMessage = FulfillBlockBodiesRequest(getBlockBodies.EthMessage);
            Send(new BlockBodiesMessage(getBlockBodies.RequestId, ethBlockBodiesMessage, int.MaxValue));
        }
        
        public void Handle(GetPooledTransactionsMessage getPooledTransactions)
        {
            Eth.V65.PooledTransactionsMessage pooledTransactionsMessage =
                FulfillPooledTransactionsRequest(getPooledTransactions.EthMessage);
            Send(new PooledTransactionsMessage(getPooledTransactions.RequestId, pooledTransactionsMessage));
        }
    }
}
