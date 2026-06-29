// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Ripemd160Precompile : IPrecompile<Ripemd160Precompile>
{
    public static Ripemd160Precompile Instance { get; } = new();

    private Ripemd160Precompile() { }

    public static Address Address { get; } = Address.FromNumber(3);

    public static string Name => "RIPEMD160";

    public ulong BaseGasCost(IReleaseSpec releaseSpec) => 600UL;

    public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        120UL * EvmCalculations.Div32Ceiling(inputData.Length);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);
}
