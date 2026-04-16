// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// Result of an L1 call via debug_traceCall, carrying return data and actual gas consumed.
/// </summary>
public readonly record struct L1CallResult(byte[]? ReturnData, long GasUsed, bool Failed)
{
    public static L1CallResult Failure() => new(null, 0L, true);
}

/// <summary>
/// Executes read-only calls against L1 contracts via debug_traceCall and returns gas consumption + return data.
/// </summary>
public interface IL1CallProvider
{
    L1CallResult ExecuteTraceCall(Address contractAddress, UInt256 blockNumber, byte[] calldata, long gasLimit);
}
