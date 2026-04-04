// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public partial class Bls12381Fp2ToG2Precompile : IPrecompile<Bls12381Fp2ToG2Precompile>
{
    public static readonly Bls12381Fp2ToG2Precompile Instance = new();

    private Bls12381Fp2ToG2Precompile() { }

    public static Address Address { get; } = Address.FromNumber(0x11);

    public static string Name => "BLS12_MAP_FP2_TO_G2";

    public long BaseGasCost(IReleaseSpec _) => 23800L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) => 0L;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData) => inputData.Length == 2 * Eip2537.LenFp;
}
