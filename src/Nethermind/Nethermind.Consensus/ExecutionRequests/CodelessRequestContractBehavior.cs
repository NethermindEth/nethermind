// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.ExecutionRequests;

/// <summary>What an EIP-7002 or EIP-7251 request predeploy without code does to the block.</summary>
public enum CodelessRequestContractBehavior
{
    /// <summary>The block is invalid, as the EIPs require.</summary>
    RejectBlock,

    /// <summary>
    /// The contract contributes no requests and is not called, leaving the state as a call to an empty account does.
    /// Not supported once EIP-7928 block access lists would record that call.
    /// </summary>
    ProduceNoRequests,
}
