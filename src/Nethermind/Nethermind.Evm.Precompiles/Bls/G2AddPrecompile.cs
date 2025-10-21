// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G2AddPrecompile : IPrecompile<G2AddPrecompile>
{
    public static readonly G2AddPrecompile Instance = new();

    private G2AddPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0d);

    public static string Name => "BLS12_G2ADD";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 600L;

    public Result<long> DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsG2AddPrecompile++;

        const int expectedInputLength = 2 * BlsConst.LenG2;
        if (inputData.Length != expectedInputLength) return Errors.InvalidInputLength;

        G2 x = new(stackalloc long[G2.Sz]);
        G2 y = new(stackalloc long[G2.Sz]);
        string? error = x.TryDecodeRaw(inputData[..BlsConst.LenG2].Span);
        if (error is not Errors.NoError) return error;

        error = y.TryDecodeRaw(inputData[BlsConst.LenG2..].Span);
        if (error is not Errors.NoError) return error;

        // adding to infinity point has no effect
        if (x.IsInf()) return inputData[BlsConst.LenG2..].ToArray();

        if (y.IsInf()) return inputData[..BlsConst.LenG2].ToArray();

        G2 res = x.Add(y);
        return res.EncodeRaw();
    }
}
