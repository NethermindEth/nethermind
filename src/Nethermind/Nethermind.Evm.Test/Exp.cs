//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bn256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Exp
    {
        [Test]
        public void T()
        {
            var input = "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b3625f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd5850b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cf";
            var input2 = "0341b65d1b32805aedf29c4704ae125b98bb9b736d6e05bd934320632bf46bb60d22bc985718acbcf51e3740c1565f66ff890dfd2302fc51abc999c83d8774ba08ed1b33fe3cd3b1ac11571999e8f451f5bb28dd4019e58b8d24d91cf73dc38f11be2878bb118612a7627f022aa19a17b6eb599bba4185df357f81d052fff90b";
            var res = "0a6678fd675aa4d8f0d03a1feb921a27f38ebdcb860cc083653519655acd6d79172fd5b3b2bfdd44e43bcec3eace9347608f9f0a16f1e184cb3f52e6f259cbeb";

            byte[] inputBytes = Bytes.FromHexString(input2);
            IPrecompile precompile = Bn256AddPrecompile.Instance;
            var result = precompile.Run(inputBytes);
        }
    }
}