// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Mev.Data;

namespace Nethermind.Mev
{
    // ReSharper disable once ClassNeverInstantiated.Global

    [RpcModule(ModuleType.Mev)]
    public interface IMevRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Adds bundle to the tx pool.", IsImplemented = true)]
        ResultWrapper<bool> eth_sendBundle(MevBundleRpc mevBundleRpc);

        [JsonRpcMethod(Description = "Adds megabundle to the tx pool.", IsImplemented = true)]
        ResultWrapper<bool> eth_sendMegabundle(MevMegabundleRpc mevMegabundleRpc);

        [JsonRpcMethod(Description = "Simulates the bundle behaviour.", IsImplemented = true)]
        ResultWrapper<TxsResults> eth_callBundle(MevCallBundleRpc mevBundleRpc);
    }
}
