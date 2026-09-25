// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.ExecutionRequests;

/// <summary>What an EIP-7002 or EIP-7251 request predeploy without code does to the block.</summary>
public enum CodelessRequestContractBehavior
{
    /// <summary>The block is invalid, as the EIPs require.</summary>
    RejectBlock,

    /// <summary>
    /// The contract is called as an empty account: the call returns no data, so it contributes no requests, and it
    /// still touches the address as the call it replaces would (e.g. for EIP-7928 block access lists).
    /// </summary>
    CallAsEmptyAccount,
}
