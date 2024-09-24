// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

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

    public static Address Address { get; } = Address.FromNumber(0x0e);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 800L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 2 * BlsParams.LenG2;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        try
        {
            G2 x = new G2(stackalloc long[G2.Sz]);
            x.DecodeRaw(inputData[..BlsParams.LenG2].Span);

            G2 y = new G2(stackalloc long[G2.Sz]);
            y.DecodeRaw(inputData[BlsParams.LenG2..].Span);

            if (x.IsInf())
            {
                // x == inf
                return (inputData[BlsParams.LenG2..], true);
            }

            if (y.IsInf())
            {
                // y == inf
                return (inputData[..BlsParams.LenG2], true);
            }

            G2 res = x.Add(y);
            return (res.EncodeRaw(), true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }
    }
}
