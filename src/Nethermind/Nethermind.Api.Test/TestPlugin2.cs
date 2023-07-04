// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api.Extensions;

namespace Nethermind.Api.Test
{
    public class TestPlugin2 : INethermindPlugin
    {
        public ValueTask DisposeAsync()
        {
            throw new System.NotImplementedException();
        }

        public string Name { get; }
        public string Description { get; }
        public string Author { get; }
        public Task Init(INethermindApi nethermindApi)
        {
            throw new System.NotImplementedException();
        }

        public Task InitNetworkProtocol()
        {
            throw new System.NotImplementedException();
        }

        public Task InitRpcModules()
        {
            throw new System.NotImplementedException();
        }
    }
}
