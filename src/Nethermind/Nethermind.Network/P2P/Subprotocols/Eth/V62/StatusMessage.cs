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

using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public class StatusMessage : P2PMessage
    {
        public byte ProtocolVersion { get; set; }
        public UInt256 ChainId { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public Keccak BestHash { get; set; }
        public Keccak GenesisHash { get; set; }
        public ForkId? ForkId { get; set; }

        public override int PacketType { get; } = Eth62MessageCode.Status;
        public override string Protocol { get; } = "eth";

        public override string ToString()
        {
            return $"{Protocol}.{ProtocolVersion} chain: {ChainId} | diff: {TotalDifficulty} | best: {BestHash.ToShortString()} | genesis: {GenesisHash.ToShortString()}";
        }
    }
}