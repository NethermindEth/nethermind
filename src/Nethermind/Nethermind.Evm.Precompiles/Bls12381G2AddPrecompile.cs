// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public partial class Bls12381G2AddPrecompile : IPrecompile<Bls12381G2AddPrecompile>
{
    public static Bls12381G2AddPrecompile Instance { get; } = new();

    private Bls12381G2AddPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x0d);

    public static string Name => "BLS12_G2ADD";

    public long BaseGasCost(IReleaseSpec _) => 600L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) => 0L;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData) => inputData.Length == 2 * Eip2537.LenG2;
}
