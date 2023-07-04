// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto.Blake2;

namespace Nethermind.Evm.Precompiles
{
    public class Blake2FPrecompile : IPrecompile
    {
        private const int RequiredInputLength = 213;

        private Blake2Compression _blake = new();

        public static readonly IPrecompile Instance = new Blake2FPrecompile();

        public Address Address { get; } = Address.FromNumber(9);

        public long BaseGasCost(IReleaseSpec releaseSpec) => 0;

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
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

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            if (inputData.Length != RequiredInputLength)
            {
                return (Array.Empty<byte>(), false);
            }

            byte finalByte = inputData.Span[212];
            if (finalByte != 0 && finalByte != 1)
            {
                return (Array.Empty<byte>(), false);
            }

            byte[] result = new byte[64];
            _blake.Compress(inputData.Span, result);

            return (result, true);
        }
    }
}
