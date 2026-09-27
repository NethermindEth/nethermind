// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ExecutionRequests;

namespace Nethermind.Eez.Execution;

public static class EezExecutionRequests
{
    public static ExecutionRequestsOptions Options { get; } = new()
    {
        CodelessRequestContracts = CodelessRequestContractBehavior.ProduceNoRequests,
    };
}
