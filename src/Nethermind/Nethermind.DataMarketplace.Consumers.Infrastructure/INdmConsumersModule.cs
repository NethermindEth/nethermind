// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure
{
    [RpcModule(ModuleType.NdmConsumer)]
    public interface INdmConsumersModule : IRpcModule
    {
        Task Init();
        void InitRpcModules();
    }
}
