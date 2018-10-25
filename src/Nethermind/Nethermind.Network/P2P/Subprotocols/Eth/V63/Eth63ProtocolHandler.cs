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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public class Eth63ProtocolHandler : Eth62ProtocolHandler
    {
        [Todo(Improve.Refactor, "reuse base mssage types from eth62")]
        private static readonly Dictionary<int, Type> MessageTypes = new Dictionary<int, Type>
        {
            {Eth62MessageCode.Status, typeof(StatusMessage)},
            {Eth62MessageCode.NewBlockHashes, typeof(NewBlockHashesMessage)},
            {Eth62MessageCode.Transactions, typeof(TransactionsMessage)},
            {Eth62MessageCode.GetBlockHeaders, typeof(GetBlockHeadersMessage)},
            {Eth62MessageCode.BlockHeaders, typeof(BlockHeadersMessage)},
            {Eth62MessageCode.GetBlockBodies, typeof(GetBlockBodiesMessage)},
            {Eth62MessageCode.BlockBodies, typeof(BlockBodiesMessage)},
            {Eth62MessageCode.NewBlock, typeof(NewBlockMessage)},
            {Eth62MessageCode.NewBlock, typeof(NewBlockMessage)},
            {Eth63MessageCode.GetNodeData, typeof(GetNodeDataMessage)},
            {Eth63MessageCode.NodeData, typeof(NodeDataMessage)},
            {Eth63MessageCode.GetReceipts, typeof(GetReceiptsMessage)},
            {Eth63MessageCode.Receipts, typeof(ReceiptsMessage)}
        };

        public Eth63ProtocolHandler(
            IP2PSession p2PSession,
            IMessageSerializationService serializer,
            ISynchronizationManager syncManager,
            ILogManager logManager, IPerfService perfService) : base(p2PSession, serializer, syncManager, logManager, perfService)
        {
        }

        public override bool IsFastSyncSupported => true;

        public override byte ProtocolVersion => 63;

        public override int MessageIdSpaceSize => 17; // magic number here following Go

        public override Type ResolveMessageType(int messageCode)
        {
            return MessageTypes[messageCode];
        }

        public override void HandleMessage(Packet message)
        {
            base.HandleMessage(message);

            switch (message.PacketType)
            {
                case Eth63MessageCode.GetReceipts:
                    Handle(Deserialize<GetReceiptsMessage>(message.Data));
                    break;
                case Eth63MessageCode.Receipts:
                    Handle(Deserialize<ReceiptsMessage>(message.Data));
                    break;
                case Eth63MessageCode.GetNodeData:
                    Handle(Deserialize<GetNodeDataMessage>(message.Data));
                    break;
                case Eth63MessageCode.NodeData:
                    Handle(Deserialize<NodeDataMessage>(message.Data));
                    break;
            }
        }

        private void Handle(GetReceiptsMessage msg)
        {
            TransactionReceipt[][] receipts = SyncManager.GetReceipts(msg.BlockHashes);
            Send(new ReceiptsMessage(receipts));
        }

        private void Handle(ReceiptsMessage msg)
        {
            throw new NotImplementedException();
        }

        private void Handle(GetNodeDataMessage msg)
        {
            byte[][] nodeData = SyncManager.GetNodeData(msg.Keys);
            Send(new NodeDataMessage(nodeData));
        }

        private void Handle(NodeDataMessage msg)
        {
            throw new NotImplementedException();
        }

        public override async Task<byte[][]> GetNodeData(Keccak[] hashes)
        {
            return await base.GetNodeData(hashes);
        }

        public override void SendNodeData(byte[][] values)
        {
            base.SendNodeData(values);
        }

        public override void SendReceipts(TransactionReceipt[][] receipts)
        {
            base.SendReceipts(receipts);
        }

        public override async Task<TransactionReceipt[][]> GetReceipts(Keccak[] blockHashes)
        {
            return await base.GetReceipts(blockHashes);
        }
    }
}