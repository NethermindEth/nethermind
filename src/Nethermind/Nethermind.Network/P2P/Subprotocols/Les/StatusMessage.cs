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

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class StatusMessage : P2PMessage
    {
        public static class KeyNames
        {
            public const string ProtocolVersion = "protocolVersion";
            public const string ChainId = "networkId";
            public const string TotalDifficulty = "headTd";
            public const string BestHash = "headHash";
            public const string HeadBlockNo = "headNum";
            public const string GenesisHash = "genesisHash";
            public const string AnnounceType = "announceType";
            public const string ServeHeaders = "serveHeaders";
            public const string ServeChainSince = "serveChainSince";
            public const string ServeRecentChain = "serveRecentChain";
            public const string ServeStateSince = "serveStateSince";
            public const string ServeRecentState = "serveRecentState";
            public const string TxRelay = "txRelay";
            public const string BufferLimit = "flowControl/BL";
            public const string MaximumRechargeRate = "flowControl/MRR";
            public const string MaximumRequestCosts = "flowControl/MRC";
        }

        public override int PacketType { get; } = LesMessageCode.Status;
        public override string Protocol => P2P.Protocol.Les;
        public byte ProtocolVersion { get; set; }
        public UInt256 ChainId { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public Keccak BestHash { get; set; }
        public long HeadBlockNo { get; set; }
        public Keccak GenesisHash { get; set; }
        #region optional
        public byte? AnnounceType { get; set; } // sent from client only
        public bool ServeHeaders { get; set; }
        public long? ServeChainSince { get; set; }
        public long? ServeRecentChain { get; set; }
        public long? ServeStateSince { get; set; }
        public long? ServeRecentState { get; set; }
        public bool TxRelay { get; set; }
        public int? BufferLimit { get; set; }
        public int? MaximumRechargeRate { get; set; }
        // These are the initial defaults from geth.
        // It probably doesn't make sense to define them here in the finished implementation, since the client will want to use the values supplied by the server

        // TODO: Benchmark finished implementation and update based on our actual serve times.
        // TODO: Implement cost scaling to account for users with different capabilities - https://github.com/ethereum/go-ethereum/blob/01d92531ee0993c0e6e5efe877a1242bfd808626/les/costtracker.go#L437
        // TODO: Implement multiple cost lists, so it can be limited based on the minimum of available bandwidth, cpu time, etc. - https://github.com/ethereum/go-ethereum/blob/01d92531ee0993c0e6e5efe877a1242bfd808626/les/costtracker.go#L186
        // TODO: This might be better as a dictionary
        public RequestCostItem[] MaximumRequestCosts = CostTracker.DefaultRequestCostTable;
        #endregion
    }
}
