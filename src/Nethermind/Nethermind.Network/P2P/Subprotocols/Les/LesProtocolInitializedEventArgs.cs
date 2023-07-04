// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.EventArg;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    // todo - I don't think we actually need all of these. prune later.
    public class LesProtocolInitializedEventArgs : ProtocolInitializedEventArgs
    {
        public string Protocol { get; set; }
        public byte ProtocolVersion { get; set; }
        public long ChainId { get; set; }
        public BigInteger TotalDifficulty { get; set; }
        public Keccak BestHash { get; set; }
        public long HeadBlockNo { get; set; }
        public Keccak GenesisHash { get; set; }
        public byte AnnounceType { get; set; }
        public bool ServeHeaders { get; set; }
        public long? ServeChainSince { get; set; }
        public long? ServeRecentChain { get; set; }
        public long? ServeStateSince { get; set; }
        public long? ServeRecentState { get; set; }
        public bool TxRelay { get; set; }
        public int? BufferLimit { get; set; }
        public int? MaximumRechargeRate { get; set; }
        public RequestCostItem[] MaximumRequestCosts { get; set; }
        public LesProtocolInitializedEventArgs(LesProtocolHandler protocolHandler) : base(protocolHandler)
        {

        }
    }
}
