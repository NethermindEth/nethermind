// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallTests : VirtualMachineTestsBase
    {
        protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Stack_underflow_on_call(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 21020, code);
            Assert.That(result.Error, Is.EqualTo("StackUnderflow"));
        }

        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Out_of_gas_on_call(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 21020, code);
            Assert.That(result.Error, Is.EqualTo("OutOfGas"));
        }
    }
}
