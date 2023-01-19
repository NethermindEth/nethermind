// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66
{
    public static class Eth66MessageCode
    {
        public const int GetBlockHeaders = Eth62MessageCode.GetBlockHeaders;
        public const int BlockHeaders = Eth62MessageCode.BlockHeaders;
        public const int GetBlockBodies = Eth62MessageCode.GetBlockBodies;
        public const int BlockBodies = Eth62MessageCode.BlockBodies;
        public const int GetPooledTransactions = Eth65MessageCode.GetPooledTransactions;
        public const int PooledTransactions = Eth65MessageCode.PooledTransactions;
        public const int GetNodeData = Eth63MessageCode.GetNodeData;
        public const int NodeData = Eth63MessageCode.NodeData;
        public const int GetReceipts = Eth63MessageCode.GetReceipts;
        public const int Receipts = Eth63MessageCode.Receipts;
    }
}
