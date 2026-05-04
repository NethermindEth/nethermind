// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// A precompile that needs the current block's L1 origin to validate user-supplied L1 block
/// numbers against the 256-block lookback window. The dispatcher reads <c>l1Origin</c> from
/// <see cref="IL1OriginStore"/> per precompile call, keyed by the L2 block number installed via
/// <c>SetBlockExecutionContext</c>, and passes it as an argument; <c>null</c> signals "no origin
/// available" (preconf blocks, eth_call/debug_traceCall before the chain has any origins) and
/// the precompile must treat that as permissive.
/// </summary>
public interface IL1OriginAware : IPrecompile
{
    Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, UInt256? l1Origin);
}

/// <summary>
/// A precompile that combines <see cref="IPrecompileGasAware"/> with <see cref="IL1OriginAware"/>:
/// it computes its own dynamic gas cost <em>and</em> needs the L1 origin for range validation.
/// </summary>
public interface IPrecompileGasAndOriginAware : IPrecompileGasAware, IL1OriginAware
{
    Result<(byte[] returnValue, long gasConsumed)> Run(
        ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, long remainingGas, UInt256? l1Origin);
}
