// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Precompiles;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Xdc;

/// <summary>Occupies the EIP-4844 point evaluation address with a precompile that fails every call.</summary>
/// <remarks>
/// XDPoSChain does not implement point evaluation. From Prague it registers an always-failing stub at 0x0a, so a
/// call fails and consumes all the gas handed to the frame instead of succeeding as a call to an empty account.
/// The name and gas cost are the upstream ones. Before Prague, 0x0a is not a precompile at all; see
/// <see cref="Spec.XdcReleaseSpec.BuildPrecompilesCache"/>.
/// </remarks>
internal sealed class XdcKzgStubPrecompile : IPrecompile<XdcKzgStubPrecompile>
{
    public static XdcKzgStubPrecompile Instance { get; } = new();

    public static Address Address => PrecompiledAddresses.PointEvaluation;

    public string Name => "KZG_POINT_EVALUATION";

    public bool SupportsCaching => false;

    public ulong BaseGasCost(IReleaseSpec releaseSpec) => 50_000UL;

    public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0UL;

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        "kzg point evaluation precompile is not supported";
}

/// <summary>Replaces the point evaluation precompile with <see cref="XdcKzgStubPrecompile"/>.</summary>
internal sealed class XdcPrecompileProvider(IPrecompileProvider inner) : IPrecompileProvider
{
    private readonly FrozenDictionary<AddressAsKey, CodeInfo> _precompiles =
        new Dictionary<AddressAsKey, CodeInfo>(inner.GetPrecompiles())
        {
            [XdcKzgStubPrecompile.Address] = new(XdcKzgStubPrecompile.Instance),
        }.ToFrozenDictionary();

    public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => _precompiles;
}
