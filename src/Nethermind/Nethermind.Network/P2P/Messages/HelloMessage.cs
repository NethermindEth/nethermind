// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class HelloMessage : P2PMessage
    {
        public override string Protocol => "p2p";
        public override int PacketType => P2PMessageCode.Hello;
        public byte P2PVersion { get; set; }
        public string ClientId { get; set; }
        public int ListenPort { get; set; }
        public PublicKey NodeId { get; set; }
        public List<Capability> Capabilities { get; set; }

        public override string ToString() => $"Hello({ClientId}, {string.Join(", ", Capabilities)})";
    }
}
