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

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Mcl.Bls;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class BlsMapToG2Tests
    {
        [Test]
        public void Test()
        {
            foreach (var (input, expectedResult) in Inputs)
            {
                IPrecompile precompile = MapToG1Precompile.Instance;
                (byte[] output, bool success) = precompile.Run(input);
                
                Console.WriteLine(Bytes.AreEqual(output, expectedResult));
                // output.Should().BeEquivalentTo(expectedResult);
                // success.Should().BeTrue();
            }
        }

        /// <summary>
        /// https://github.com/matter-labs/eip1962/tree/master/src/test/test_vectors/eip2537
        /// </summary>
        private static readonly Dictionary<byte[], byte[]> Inputs = new Dictionary<byte[], byte[]>
        {
        };
    }
}