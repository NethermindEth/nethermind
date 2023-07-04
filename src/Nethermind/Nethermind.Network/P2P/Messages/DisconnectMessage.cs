// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Messages
{
    public class DisconnectMessage : P2PMessage
    {
        public DisconnectMessage(DisconnectReason reason)
        {
            Reason = (int)reason;
        }

        public DisconnectMessage(int reason)
        {
            Reason = reason;
        }

        public override string Protocol => "p2p";
        public override int PacketType => P2PMessageCode.Disconnect;
        public int Reason { get; }

        public override string ToString() => $"Disconnect({Reason})";
    }
}
