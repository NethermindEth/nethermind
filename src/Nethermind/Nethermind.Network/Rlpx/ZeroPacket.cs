// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;

namespace Nethermind.Network.Rlpx
{
    public class ZeroPacket : DefaultByteBufferHolder
    {
        public ZeroPacket(Packet packet) : base(Unpooled.CopiedBuffer(packet.Data))
        {
            Protocol = packet.Protocol;
            PacketType = (byte)packet.PacketType;
        }

        public string Protocol { get; set; }
        public byte PacketType { get; set; }

        public ZeroPacket(IByteBuffer data) : base(data)
        {
        }
    }
}
