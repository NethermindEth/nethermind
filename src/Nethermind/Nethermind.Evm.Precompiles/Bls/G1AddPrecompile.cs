// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G1AddPrecompile : IPrecompile<G1AddPrecompile>
{
    public static readonly G1AddPrecompile Instance = new();

    private G1AddPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0b);

    public static string Name => "BLS12_G1ADD";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 375L;

    public Result<long> DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsG1AddPrecompile++;

        const int expectedInputLength = 2 * BlsConst.LenG1;
        if (inputData.Length != expectedInputLength) return Errors.InvalidInputLength;

        G1 x = new(stackalloc long[G1.Sz]);
        G1 y = new(stackalloc long[G1.Sz]);
        string? error = x.TryDecodeRaw(inputData[..BlsConst.LenG1].Span);
        if (error is not Errors.NoError) return error;

        error = y.TryDecodeRaw(inputData[BlsConst.LenG1..].Span);
        if (error is not Errors.NoError) return error;

        // adding to infinity point has no effect
        if (x.IsInf()) return inputData[BlsConst.LenG1..].ToArray();

        if (y.IsInf()) return inputData[..BlsConst.LenG1].ToArray();

        G1 res = x.Add(y);
        return res.EncodeRaw();
    }
}
