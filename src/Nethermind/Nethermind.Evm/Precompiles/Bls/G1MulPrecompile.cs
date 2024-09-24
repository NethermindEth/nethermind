// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.ExceptionServices;
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
        const int expectedInputLength = BlsParams.LenG1 + BlsParams.LenFr;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        try
        {
            G1 x = new G1(stackalloc long[G1.Sz]);
            x.DecodeRaw(inputData[..BlsParams.LenG1].Span);

            if (x.IsInf())
            {
                return (Enumerable.Repeat<byte>(0, 128).ToArray(), true);
            }

            if (!x.InGroup())
            {
                return IPrecompile.Failure;
            }

            if (!inputData.Span[BlsParams.LenG1..].ContainsAnyExcept((byte)0))
            {
                return (Enumerable.Repeat<byte>(0, 128).ToArray(), true);
            }

            Span<byte> scalar = stackalloc byte[32];
            for (int i = 0; i < 32; i++)
            {
                scalar[32 - i - 1] = inputData.Span[BlsParams.LenG1 + i];
            }

            G1 res = x.Mult(scalar);
            return (res.EncodeRaw(), true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }
    }
}
