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

    public static ExecutionRequestsOptions WithoutRequests { get; } = new()
    {
        CodelessRequestContracts = CodelessRequestContractBehavior.ProduceNoRequests,
        ReadsRequests = false,
    };

    public static ExecutionRequestsOptions For(EezChainRules rules) => rules.ReadsExecutionRequests ? Options : WithoutRequests;
}
