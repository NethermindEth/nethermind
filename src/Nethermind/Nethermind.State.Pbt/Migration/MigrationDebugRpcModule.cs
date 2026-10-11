// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.State.Pbt.Migration;

public sealed class MigrationDebugRpcModule(IMigrationTelemetry telemetry) : IMigrationDebugRpcModule
{
    public ResultWrapper<MigrationProgressForRpc> debug_migrationProgress() =>
        ResultWrapper<MigrationProgressForRpc>.Success(telemetry.GetProgress());

    public ResultWrapper<Hash256?> debug_shadowStateRoot(Hash256 blockHash) =>
        ResultWrapper<Hash256?>.Success(telemetry.GetShadowRoot(blockHash));
}
