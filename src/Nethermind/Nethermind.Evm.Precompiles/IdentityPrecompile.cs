// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public class IdentityPrecompile : IPrecompile<IdentityPrecompile>
{
    public static readonly IdentityPrecompile Instance = new();

    private IdentityPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(4);

    public static string Name => "ID";

    // Caching disabled: the copy operation is O(n) and the cache key hash is also O(n),
    // making caching strictly worse than direct execution for this precompile.
    public bool SupportsCaching => false;

    public long BaseGasCost(IReleaseSpec releaseSpec) => 15L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        3L * EvmCalculations.Div32Ceiling((ulong)inputData.Length);

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => inputData.ToArray();
}
