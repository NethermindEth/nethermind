﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
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