/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class BlockParameterTests
    {
        [TestCase("0x0", 0)]
        [TestCase("0x00", 0)]
        [TestCase("0x1", 1)]
        [TestCase("0x01", 1)]
        [TestCase("0x8180", 33152)]
        public void As_number_returns_correct_value(string input, int output)
        {
            BlockParameter blockParameter = new BlockParameter();
            blockParameter.BlockId = new Quantity(input);
            Assert.AreEqual((UInt256)output, blockParameter.BlockId.AsNumber() ?? 0, "hex string");
            
            blockParameter.BlockId = new Quantity(output);
            Assert.AreEqual((UInt256)output, blockParameter.BlockId.AsNumber() ?? 0, "big integer");
        }
    }
}