// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.FullPruning;

namespace Nethermind.JsonRpc.Modules.Admin;

public class PruningTrieStateAdminRpcModule(
    ManualPruningTrigger manualPruningTrigger
) : IPruningTrieStateAdminRpcModule
{
    public ResultWrapper<PruningStatus> admin_prune() => ResultWrapper<PruningStatus>.Success(manualPruningTrigger.Trigger());
}
