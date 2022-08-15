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

using System.Diagnostics;

namespace Nethermind.Network.Rlpx
{
    [DebuggerDisplay("{Protocol}.{PacketType}")]
    public class Packet
    {
        public byte[] Data;

        public Packet(ZeroPacket zeroPacket)
        {
            Data = zeroPacket.Content.ReadAllBytes();
            PacketType = zeroPacket.PacketType;
            Protocol = zeroPacket.Protocol;
        }
        
        public Packet(string protocol, int packetType, byte[] data)
        {
            Data = data;
            Protocol = protocol;
            PacketType = packetType;
        }

        public Packet(byte[] data)
        {
            Data = data;
        }

        public int PacketType { get; set; }

        public string Protocol { get; set; }

        public override string ToString()
        {
            return $"{Protocol ?? "???"}.{PacketType}";
        }
    }
}
