// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public partial class Bls12381G2MsmPrecompile : IPrecompile<Bls12381G2MsmPrecompile>
{
    public const int ItemSize = 288;

    public static Bls12381G2MsmPrecompile Instance { get; } = new();

    private Bls12381G2MsmPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0xe);

    public static string Name => "BLS12_G2MSM";

    public long BaseGasCost(IReleaseSpec _) => 0L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        int k = inputData.Length / ItemSize;
        return 22500L * k * Eip2537.DiscountForG2(k) / 1000;
    }

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData) =>
        inputData.Length != 0 && inputData.Length % ItemSize == 0;
}
