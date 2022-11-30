// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    internal class SessionClientForRpc
    {
        public string? Id { get; set; }
        public bool StreamEnabled { get; set; }
        public string[]? Args { get; set; }

        public SessionClientForRpc()
        {
        }

        public SessionClientForRpc(SessionClient session)
        {
            Id = session.Id;
            StreamEnabled = session.StreamEnabled;
            Args = session.Args;
        }
    }
}
