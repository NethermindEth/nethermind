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

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 120L * EvmPooledMemory.Div32Ceiling((ulong)inputData.Length);
        }

        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.Ripemd160Precompile++;
            
            // missing in .NET Core
//            return _ripemd.ComputeHash(inputData).PadLeft(32);
            return (Ripemd.Compute(inputData.ToArray()).PadLeft(32), true);
        }
    }
}
