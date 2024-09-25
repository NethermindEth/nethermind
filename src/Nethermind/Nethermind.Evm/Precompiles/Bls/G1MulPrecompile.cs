// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G1MulPrecompile : IPrecompile<G1MulPrecompile>
{
    public static readonly G1MulPrecompile Instance = new();

    private G1MulPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0c);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 12000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = BlsConst.LenG1 + BlsConst.LenFr;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        G1 x = new(stackalloc long[G1.Sz]);
        if (!x.TryDecodeRaw(inputData[..BlsConst.LenG1].Span) || !x.InGroup())
        {
            return IPrecompile.Failure;
        }

        bool scalarIsInfinity = !inputData.Span[BlsConst.LenG1..].ContainsAnyExcept((byte)0);
        if (scalarIsInfinity || x.IsInf())
        {
            return (BlsConst.G1Inf, true);
        }

        Span<byte> scalar = stackalloc byte[32];
        inputData.Span[BlsConst.LenG1..].CopyTo(scalar);
        scalar.Reverse();

        G1 res = x.Mult(scalar);
        return (res.EncodeRaw(), true);
    }
}
