// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class StatusMessage : P2PMessage
    {
        public byte ProtocolVersion { get; set; }
        public UInt256 NetworkId { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public Keccak? BestHash { get; set; }
        public Keccak? GenesisHash { get; set; }
        public ForkId? ForkId { get; set; }

        public override int PacketType { get; } = Eth62MessageCode.Status;
        public override string Protocol { get; } = "eth";

        public override string ToString()
        {
            return $"{Protocol}.{ProtocolVersion} network: {NetworkId} | diff: {TotalDifficulty} | best: {BestHash?.ToShortString()} | genesis: {GenesisHash?.ToShortString()} | fork: {ForkId}";
        }
    }
}
