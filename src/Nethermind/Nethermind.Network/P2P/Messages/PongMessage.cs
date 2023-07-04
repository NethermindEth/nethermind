// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Messages
{
    public class PongMessage : P2PMessage
    {
        public static readonly PongMessage Instance = new();

        private PongMessage()
        {
        }

        public override string Protocol => "p2p";
        public override int PacketType => P2PMessageCode.Pong;

        public override string ToString() => "Pong";
    }
}
