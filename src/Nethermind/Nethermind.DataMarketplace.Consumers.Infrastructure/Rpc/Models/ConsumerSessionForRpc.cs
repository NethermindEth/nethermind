// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rpc.Models;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class ConsumerSessionForRpc : SessionForRpc
    {
        public uint ConsumedUnitsFromProvider { get; }
        public string DataAvailability { get; }
        public SessionClientForRpc[] Clients { get; }

        public ConsumerSessionForRpc(ConsumerSession session) : base(session)
        {
            ConsumedUnitsFromProvider = session.ConsumedUnitsFromProvider;
            DataAvailability = session.DataAvailability.ToString().ToLowerInvariant();
            Clients = session.Clients.Select(c => new SessionClientForRpc(c)).ToArray();
        }
    }
}
