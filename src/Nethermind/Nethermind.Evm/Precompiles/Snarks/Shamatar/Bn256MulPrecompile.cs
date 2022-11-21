// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto.Bls;

namespace Nethermind.Evm.Precompiles.Snarks.Shamatar
{
    /// <summary>
    /// https://github.com/herumi/mcl/blob/master/api.md
    /// </summary>
    public class Bn256MulPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new Bn256MulPrecompile();

        public Address Address { get; } = Address.FromNumber(7);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return releaseSpec.IsEip1108Enabled ? 6000L : 40000L;
        }

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.Bn256MulPrecompile++;
            Span<byte> inputDataSpan = stackalloc byte[96];
            inputData.PrepareEthInput(inputDataSpan);

            Span<byte> output = stackalloc byte[64];
            bool success = ShamatarLib.Bn256Mul(inputDataSpan, output);

            (byte[], bool) result;
            if (success)
            {
                result = (output.ToArray(), true);
            }
            else
            {
                result = (Array.Empty<byte>(), false);
            }

            return result;
        }
    }
}
