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
    public class PairingPrecompile : IPrecompile
    {
        private const int PairSize = 384;

        private PairingPrecompile() { }

        public Address Address { get; } = Address.FromNumber(16);

        public static IPrecompile Instance = new PairingPrecompile();

        public long BaseGasCost(IReleaseSpec releaseSpec) => 115000L;

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 23000L * (inputData.Length / PairSize);
        }

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            if (inputData.Length % PairSize > 0 || inputData.Length == 0)
            {
                return (Array.Empty<byte>(), false);
            }

            (byte[], bool) result;

            Span<byte> output = stackalloc byte[32];
            bool success = ShamatarLib.BlsPairing(inputData.Span, output);
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
