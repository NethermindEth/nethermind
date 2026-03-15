// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public partial class Bls12381G1MsmPrecompile : IPrecompile<Bls12381G1MsmPrecompile>
{
    public const int ItemSize = 160;

    public static readonly Bls12381G1MsmPrecompile Instance = new();

    private Bls12381G1MsmPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x0c);

    public static string Name => "BLS12_G1MSM";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 12000L * k * Eip2537.DiscountForG1(k) / 1000;
    }

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData) =>
        inputData.Length != 0 && inputData.Length % ItemSize == 0;
}
