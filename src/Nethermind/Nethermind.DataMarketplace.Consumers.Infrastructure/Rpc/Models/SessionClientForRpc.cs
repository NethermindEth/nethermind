// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Consumers.Sessions.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class SessionClientForRpc
    {
        public string Id { get; }
        public bool StreamEnabled { get; }
        public string?[] Args { get; }

        public SessionClientForRpc(SessionClient session)
        {
            Id = session.Id;
            StreamEnabled = session.StreamEnabled;
            Args = session.Args;
        }
    }
}
