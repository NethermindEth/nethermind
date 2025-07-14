// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto.Blake2;

namespace Nethermind.Evm.Precompiles
{
    public class Blake2FPrecompile : IPrecompile<Blake2FPrecompile>
    {
        private const int RequiredInputLength = 213;

        private readonly Blake2Compression _blake = new();

        public static readonly Blake2FPrecompile Instance = new();

        public static Address Address { get; } = Address.FromNumber(9);

        public static string Name => "BLAKE2F";

        public long BaseGasCost(IReleaseSpec releaseSpec) => 0;

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            if (inputData.Length != RequiredInputLength)
            {
                return 0;
            }

            byte finalByte = inputData.Span[212];
            if (finalByte != 0 && finalByte != 1)
            {
                return 0;
            }

            uint rounds = inputData[..4].Span.ReadEthUInt32();

            return rounds;
        }

        public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            if (inputData.Length != RequiredInputLength)
            {
                return IPrecompile.Failure;
            }

            byte finalByte = inputData.Span[212];
            if (finalByte != 0 && finalByte != 1)
            {
                return IPrecompile.Failure;
            }

            byte[] result = new byte[64];
            _blake.Compress(inputData.Span, result);

            return (result, true);
        }
    }
}
