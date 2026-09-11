// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// A precompile that wants execution-context extras from the Taiko VM (remaining gas, L1 origin,
/// future fields). Replaces the family of single-concern <c>IXxxAware</c> interfaces with a single
/// dispatch branch in <see cref="TaikoVirtualMachine{TGasPolicy}"/>; new context fields are added
/// to <see cref="PrecompileExtras"/> rather than as new interfaces.
/// </summary>
/// <remarks>
/// The returned <c>gasConsumed</c> is the dynamic gas the VM should deduct in addition to the
/// static <see cref="IPrecompile.BaseGasCost"/> + <see cref="IPrecompile.DataGasCost"/>. Precompiles
/// with no dynamic component return <c>0</c>. Callers that don't go through the Taiko VM (caching
/// layer, JSON-RPC providers, tests) use the base <see cref="IPrecompile.Run"/> overload instead.
/// </remarks>
public interface IContextAwarePrecompile : IPrecompile
{
    Result<(byte[] returnValue, ulong gasConsumed)> Run(
        ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, in PrecompileExtras extras);
}
