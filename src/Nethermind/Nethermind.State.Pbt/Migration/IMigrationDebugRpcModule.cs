// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.State.Pbt.Migration;

[RpcModule(ModuleType.Debug)]
public interface IMigrationDebugRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Returns EIP-8347 migration progress (debug telemetry only).", IsImplemented = true, IsSharable = true)]
    ResultWrapper<MigrationProgressForRpc> debug_migrationProgress();

    [JsonRpcMethod(Description = "Returns a retained shadow state root by block hash, or null when unavailable.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<Hash256?> debug_shadowStateRoot(Hash256 blockHash);
}
