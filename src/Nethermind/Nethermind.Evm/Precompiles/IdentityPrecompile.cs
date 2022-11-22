// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class IdentityPrecompile : IPrecompile
    {
        public static readonly IPrecompile Instance = new IdentityPrecompile();

        private IdentityPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(4);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 15L;
        }

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 3L * EvmPooledMemory.Div32Ceiling((ulong)inputData.Length);
        }

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return (inputData, true);
        }
    }
}
