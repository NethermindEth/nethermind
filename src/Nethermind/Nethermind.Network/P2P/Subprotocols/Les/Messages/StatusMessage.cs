// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class StatusMessage : P2PMessage
    {
        public static class KeyNames
        {
            public const string ProtocolVersion = "protocolVersion";
            public const string NetworkId = "networkId";
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
        public override string Protocol => Contract.P2P.Protocol.Les;
        public byte ProtocolVersion { get; set; }
        public UInt256 NetworkId { get; set; }
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
