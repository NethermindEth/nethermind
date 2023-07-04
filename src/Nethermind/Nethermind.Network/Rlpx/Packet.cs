// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx
{
    [DebuggerDisplay("{Protocol}.{PacketType}")]
    public class Packet
    {
        public byte[] Data;

        public Packet(ZeroPacket zeroPacket)
        {
            Data = zeroPacket.Content.ReadAllBytesAsArray();
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
