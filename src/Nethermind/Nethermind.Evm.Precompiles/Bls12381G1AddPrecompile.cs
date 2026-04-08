// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public partial class Bls12381G1AddPrecompile : IPrecompile<Bls12381G1AddPrecompile>
{
    public static readonly Bls12381G1AddPrecompile Instance = new();

    private Bls12381G1AddPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x0b);

    public static string Name => "BLS12_G1ADD";

    public long BaseGasCost(IReleaseSpec _) => 375L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) => 0L;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData) => inputData.Length == 2 * Eip2537.LenG1;
}
