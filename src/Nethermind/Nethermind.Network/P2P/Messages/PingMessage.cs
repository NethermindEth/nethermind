// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Messages
{
    public class PingMessage : P2PMessage
    {
        public static readonly PingMessage Instance = new();

        private PingMessage()
        {
        }

        public override string Protocol => "p2p";
        public override int PacketType => P2PMessageCode.Ping;

        public override string ToString() => "Ping";
    }
}
