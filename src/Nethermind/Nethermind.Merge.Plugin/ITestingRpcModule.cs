// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Merge.Plugin;

[RpcModule(ModuleType.Testing)]
public interface ITestingRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Building a block from provided transactions, under provided rules.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<object>> testing_buildBlockV1(Hash256 parentBlockHash, PayloadAttributes payloadAttributes, IEnumerable<byte[]>? txRlps, byte[]? extraData = null);

    [JsonRpcMethod(
        Description = "Build a block from provided transactions on top of the current chain head, commit it, and wait for the BlockchainProcessor to advance the canonical head to the committed block before returning. Returns the committed block hash. Concurrent invocations are serialized inside the implementation, so callers do not need their own mutex; serialization order between simultaneous calls is unspecified.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<Hash256>> testing_commitBlockV1(PayloadAttributes payloadAttributes, IEnumerable<byte[]> txRlps, byte[]? extraData = null);
}
