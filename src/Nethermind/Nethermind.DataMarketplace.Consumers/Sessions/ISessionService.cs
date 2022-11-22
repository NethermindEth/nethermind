// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Sessions
{
    public interface ISessionService
    {
        ConsumerSession? GetActive(Keccak depositId);
        IReadOnlyList<ConsumerSession> GetAllActive();
        Task StartSessionAsync(Session session, INdmPeer provider);
        Task FinishSessionAsync(Session session, INdmPeer provider, bool removePeer = true);
        Task FinishSessionsAsync(INdmPeer provider, bool removePeer = true);
        Task<Keccak?> SendFinishSessionAsync(Keccak depositId);
    }
}
