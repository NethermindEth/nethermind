// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class Eip2929Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.BerlinBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [Test]
        public void Case1()
        {
            WorldState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x60013f5060023b506003315060f13f5060f23b5060f3315060f23f5060f33b5060f1315032315030315000")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(1);
            AssertGas(result, GasCostOf.Transaction + 8653);
        }

        [Test]
        public void Case2()
        {
            WorldState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x60006000600060ff3c60006000600060ff3c600060006000303c00")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(1);
            AssertGas(result, GasCostOf.Transaction + 2835);
        }

        [Test]
        public void Case3()
        {
            WorldState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x60015450601160015560116002556011600255600254600154")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(1);
            AssertGas(result, GasCostOf.Transaction + 44529);
        }

        [Test]
        public void Case4()
        {
            WorldState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .FromCode("0x60008080808060046000f15060008080808060ff6000f15060008080808060ff6000fa50")
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(1);
            AssertGas(result, GasCostOf.Transaction + 2869);
        }

        protected override TestAllTracerWithOutput CreateTracer()
        {
            TestAllTracerWithOutput tracer = base.CreateTracer();
            tracer.IsTracingAccess = false;
            return tracer;
        }
    }
}
