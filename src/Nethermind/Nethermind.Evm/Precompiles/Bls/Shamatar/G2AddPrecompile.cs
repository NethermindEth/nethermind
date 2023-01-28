// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto.Bls;

namespace Nethermind.Evm.Precompiles.Bls.Shamatar
{
    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2537
    /// </summary>
    public class G2AddPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new G2AddPrecompile();

        private G2AddPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(13);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 4500L;
        }

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            const int expectedInputLength = 8 * BlsParams.LenFp;
            if (inputData.Length != expectedInputLength)
            {
                return (Array.Empty<byte>(), false);
            }

            // Span<byte> inputDataSpan = stackalloc byte[expectedInputLength];
            // inputData.PrepareEthInput(inputDataSpan);

            (byte[], bool) result;

            Span<byte> output = stackalloc byte[4 * BlsParams.LenFp];
            bool success = ShamatarLib.BlsG2Add(inputData.Span, output);
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
