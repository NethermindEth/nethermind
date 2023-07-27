// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            Assert.That(bigIntPair.Item1.ToArray(), Is.EqualTo(gmpPair.Item1.ToArray()));
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
            Assert.DoesNotThrow(() => ModExpPrecompile.Instance.Run(input.Done.ToArray(), London.Instance, null));
            long gas = ModExpPrecompile.Instance.DataGasCost(input.Done, London.Instance);
            gas.Should().Be(200);
        }
    }
}
