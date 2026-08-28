// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Sha256Precompile : IPrecompile<Sha256Precompile>
{
    public static Sha256Precompile Instance { get; } = new();

    private Sha256Precompile() { }

    public static Address Address { get; } = Address.FromNumber(2);

    public static string Name => "SHA256";

    public ulong BaseGasCost(IReleaseSpec releaseSpec) => 60UL;

    public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) =>
        12UL * EvmCalculations.Div32Ceiling(inputData.Length);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);
}
