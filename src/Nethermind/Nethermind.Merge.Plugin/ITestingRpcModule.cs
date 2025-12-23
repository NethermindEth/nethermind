// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

[RpcModule(ModuleType.Testing)]
public interface ITestingRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Building a block from provided transactions, under provided rules.",
        IsSharable = true,
        IsImplemented = true)]

    public Task<ResultWrapper<GetPayloadV5Result?>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]> txRlps, byte[]? extraData);
}
