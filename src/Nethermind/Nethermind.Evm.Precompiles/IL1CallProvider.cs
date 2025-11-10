// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Interface for L1 call providers that can execute calls on L1 contracts.
/// </summary>
public interface IL1CallProvider
{
    /// <summary>
    /// Executes a call on an L1 contract.
    /// </summary>
    /// <param name="contractAddress">The L1 contract address to call</param>
    /// <param name="gas">The gas limit for the call</param>
    /// <param name="value">The value to send with the call</param>
    /// <param name="callData">The call data (function selector + ABI-encoded parameters), or null/empty if no call data</param>
    /// <param name="feePerGas">The fee the user is willing to pay for executing this call on L1</param>
    /// <returns>The return data from the L1 call, or null if the call fails</returns>
    byte[]? ExecuteCall(Address contractAddress, ulong gas, UInt256 value, byte[]? callData, UInt256 feePerGas);
}
