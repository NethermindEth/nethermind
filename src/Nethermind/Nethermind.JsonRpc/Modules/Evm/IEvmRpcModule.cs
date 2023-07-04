// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Evm
{
    [RpcModule(ModuleType.Evm)]
    public interface IEvmRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Triggers block production.", IsImplemented = true, IsSharable = false)]
        ResultWrapper<bool> evm_mine();
    }
}
