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
using System.Linq;
using FluentAssertions;
using MathNet.Numerics.Random;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip2565Tests
    {
        const string Length64 = "0000000000000000000000000000000000000000000000000000000000000040";

        [Test]
        public void Simple_routine([Random(int.MinValue, int.MaxValue, 100)] int seed)
        {
            Random random = new(seed);
            byte[] data = random.NextBytes(3 * 64);
            string randomInput = string.Format("{0}{0}{0}{1}", Length64, data.ToHexString());

            Prepare input = Prepare.EvmCode.FromCode(randomInput);

            (ReadOnlyMemory<byte>, bool) gmpPair = ModExpPrecompile.Instance.Run(input.Done.ToArray(), Berlin.Instance);
#pragma warning disable 618
            (ReadOnlyMemory<byte>, bool) bigIntPair = ModExpPrecompile.OldRun(input.Done.ToArray());
#pragma warning restore 618

            Assert.AreEqual(gmpPair.Item1.ToArray(), bigIntPair.Item1.ToArray());
        }

        [Test]
        public void Overflow_gas_cost()
        {
            Prepare input = Prepare.EvmCode.FromCode("0000000000000000000000000000000000000000000000000000000000000001200000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000010001");
            long gas = ModExpPrecompile.Instance.DataGasCost(input.Done, Berlin.Instance);
            gas.Should().Be(long.MaxValue);
        }

        [TestCase("0x00000000000000000000000000000000000000000000000000000000000000008000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000800000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001")]
        public void ModExp_run_should_not_throw_exception(string inputStr)
        {
            Prepare input = Prepare.EvmCode.FromCode(inputStr);
            Assert.DoesNotThrow(() => ModExpPrecompile.Instance.Run(input.Done.ToArray(), London.Instance));
            long gas = ModExpPrecompile.Instance.DataGasCost(input.Done, London.Instance);
            gas.Should().Be(200);
        }
    }
}
