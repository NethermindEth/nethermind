// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P.Eth100.Messages
{
    /// <summary>
    /// P2P message wrapper for XDPoS v2 Timeout
    /// Propagates round timeout certificates across the network
    /// </summary>
    public class TimeoutP2PMessage : P2PMessage
    {
        public override int PacketType => Eth100MessageCode.Timeout;
        public override string Protocol => "eth";

        public Nethermind.Xdc.Types.Timeout Timeout { get; set; }

        public TimeoutP2PMessage(Nethermind.Xdc.Types.Timeout timeout)
        {
            Timeout = timeout;
        }

        public TimeoutP2PMessage()
        {
        }

        public override string ToString() => $"{nameof(TimeoutP2PMessage)}({Timeout})";
    }
}
