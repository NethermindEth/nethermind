// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Network.Rlpx
{
    [DebuggerDisplay("{Protocol}.{PacketType}")]
    public class Packet(string? protocol, int packetType, byte[] data)
    {
        public byte[] Data = data;

        public Packet(ZeroPacket zeroPacket) : this(zeroPacket.Protocol, zeroPacket.PacketType, zeroPacket.Content.ToArray())
        {
        }

        public Packet(byte[] data) : this(null, 0, data)
        {
        }

        public int PacketType { get; set; } = packetType;

        public string? Protocol { get; set; } = protocol;

        public override string ToString() => $"{Protocol ?? "???"}.{PacketType}";

        public static implicit operator ZeroPacket(Packet packet) => new ByteArrayPacket(packet);
    }
}
