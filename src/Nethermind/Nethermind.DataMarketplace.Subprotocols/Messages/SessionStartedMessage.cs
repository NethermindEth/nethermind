// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Session = Nethermind.DataMarketplace.Core.Domain.Session;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class SessionStartedMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.SessionStarted;
        public override string Protocol => "ndm";
        public Session Session { get; }

        public SessionStartedMessage(Session session)
        {
            Session = session;
        }
    }
}
