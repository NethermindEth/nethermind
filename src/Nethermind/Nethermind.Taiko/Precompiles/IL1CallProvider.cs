// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Taiko.Precompiles;

public readonly record struct L1CallResult(byte[]? ReturnData, long GasUsed, bool Failed)
{
    public static L1CallResult Failure() => new(null, 0L, true);
}

public interface IL1CallProvider
{
    L1CallResult ExecuteTraceCall(Address contractAddress, UInt256 blockNumber, byte[] calldata, long gasLimit);
}
