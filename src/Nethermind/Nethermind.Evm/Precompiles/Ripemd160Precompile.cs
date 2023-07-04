// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles
{
    public class Ripemd160Precompile : IPrecompile
    {
        public static readonly IPrecompile Instance = new Ripemd160Precompile();

        // missing in .NET Core
        //        private static RIPEMD160 _ripemd;

        private Ripemd160Precompile()
        {
            // missing in .NET Core
            //            _ripemd = RIPEMD160.Create();
            //            _ripemd.Initialize();
        }

        public Address Address { get; } = Address.FromNumber(3);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 600L;
        }

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 120L * EvmPooledMemory.Div32Ceiling((ulong)inputData.Length);
        }

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.Ripemd160Precompile++;

            return (Ripemd.Compute(inputData.ToArray()).PadLeft(32), true);
        }
    }
}
