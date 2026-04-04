// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Sha256Precompile : IPrecompile<Sha256Precompile>
{
    public static readonly Sha256Precompile Instance = new();

    private Sha256Precompile() { }

    public static Address Address { get; } = Address.FromNumber(2);

    public static string Name => "SHA256";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 60L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) =>
        12L * EvmCalculations.Div32Ceiling((ulong)inputData.Length);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);
}
