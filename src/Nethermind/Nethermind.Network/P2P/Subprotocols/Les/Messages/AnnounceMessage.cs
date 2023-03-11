// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class AnnounceMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.Announce;
        public override string Protocol => Contract.P2P.Protocol.Les;
        public Keccak HeadHash;
        public long HeadBlockNo;
        public UInt256 TotalDifficulty;
        public long ReorgDepth;
        // todo - add optional items
    }
}
