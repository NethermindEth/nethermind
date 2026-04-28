// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// A precompile that computes and reports its own dynamic gas consumption.
/// The VM deducts the reported <c>gasConsumed</c> in addition to the static
/// <see cref="IPrecompile.BaseGasCost"/> + <see cref="IPrecompile.DataGasCost"/>.
/// The canonical consumer is <see cref="L1StaticCallPrecompile"/>, where actual
/// L1 gas usage is only known after the L1 RPC call completes.
/// </summary>
public interface IPrecompileGasAware : IPrecompile
{
    Result<(byte[] returnValue, long gasConsumed)> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, long remainingGas);
}
