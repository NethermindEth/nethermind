// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.ExecutionRequests;

/// <summary>
/// Chain-specific deviations from the L1 execution-request rules.
/// </summary>
public sealed class ExecutionRequestsOptions
{
    public static ExecutionRequestsOptions Default { get; } = new();

    public CodelessRequestContractBehavior CodelessRequestContracts { get; init; } = CodelessRequestContractBehavior.RejectBlock;

    /// <summary>
    /// Whether deposit logs and the request predeploys are read. When <c>false</c>, every block where requests are
    /// enabled carries none, and its requests hash is the hash of no requests.
    /// </summary>
    public bool ReadsRequests { get; init; } = true;
}
