//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 3L * EvmPooledMemory.Div32Ceiling((ulong)inputData.Length);
        }

        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return (inputData, true);
        }
    }
}
