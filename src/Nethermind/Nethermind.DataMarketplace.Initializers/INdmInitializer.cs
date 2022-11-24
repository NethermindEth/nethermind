// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Infrastructure;

namespace Nethermind.DataMarketplace.Initializers
{
    public interface INdmInitializer
    {
        Task<INdmCapabilityConnector> InitAsync(INdmApi nethermindApi);
        void InitRpcModules();
    }
}
