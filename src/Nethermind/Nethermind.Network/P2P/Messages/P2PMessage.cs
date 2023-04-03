// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Messages
{
    public abstract class P2PMessage : MessageBase
    {
        public abstract int PacketType { get; }

        public int AdaptivePacketType { get; set; }

        public abstract string Protocol { get; }
    }
}
