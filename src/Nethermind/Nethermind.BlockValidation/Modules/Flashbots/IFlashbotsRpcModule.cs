// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.BlockValidation.Data;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.BlockValidation.Modules.Flashbots;

[RpcModule(ModuleType.Flashbots)]
public interface IFlashbotsRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = " validate the builder submissions as received by a relay",
        IsSharable = false,
        IsImplemented = true)]
    Task<ResultWrapper<BlockValidationResult>> flashbots_validateBuilderSubmissionV3(BuilderBlockValidationRequest @params);
}
