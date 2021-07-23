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
        public const int PooledTransactions  = Eth65MessageCode.PooledTransactions;
        public const int GetNodeData = Eth63MessageCode.GetNodeData;
        public const int NodeData = Eth63MessageCode.NodeData;
        public const int GetReceipts = Eth63MessageCode.GetReceipts;
        public const int Receipts = Eth63MessageCode.Receipts;
    }
}
