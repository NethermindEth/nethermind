// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public partial class Bls12381FpToG1Precompile : IPrecompile<Bls12381FpToG1Precompile>
{
    public static Bls12381FpToG1Precompile Instance { get; } = new();

    private Bls12381FpToG1Precompile() { }

    public static Address Address { get; } = Address.FromNumber(0x10);

    public static string Name => "BLS12_MAP_FP_TO_G1";

    public long BaseGasCost(IReleaseSpec _) => 5500L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) => 0L;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData) => inputData.Length == Eip2537.LenFp;
}
