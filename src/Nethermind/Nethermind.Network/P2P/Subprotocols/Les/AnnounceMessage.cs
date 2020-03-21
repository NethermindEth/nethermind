﻿using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class AnnounceMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.Announce;
        public override string Protocol => P2P.Protocol.Les;
        public Keccak HeadHash;
        public long HeadBlockNo;
        public UInt256 TotalDifficulty;
        public long ReorgDepth;
        // todo - add optional items
    }
}
