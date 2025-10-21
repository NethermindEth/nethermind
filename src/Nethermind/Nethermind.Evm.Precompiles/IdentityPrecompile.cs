// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Precompiles;

public class IdentityPrecompile : IPrecompile<IdentityPrecompile>
{
    public static readonly IdentityPrecompile Instance = new();

    private IdentityPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(4);

    public static string Name => "ID";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 15L;

    public Result<long> DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        3L * EvmCalculations.Div32Ceiling((ulong)inputData.Length);

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => inputData.ToArray();
}
