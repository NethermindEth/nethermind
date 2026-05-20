// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
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

        /// <summary>
        /// Post-EIP-150 (Tangerine Whistle and later): the 63/64 cap clamps any caller-supplied
        /// gasLimit to a value strictly less than <c>long.MaxValue</c>, so the post-clamp
        /// <c>(long)gasLimit</c> cast never trips even when the user pushes <c>UInt256.MaxValue</c>.
        /// </summary>
        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Call_with_gas_above_long_max_post_eip150_is_capped_not_out_of_gas(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)               // retLength
                .PushData(0)               // retOffset
                .PushData(0)               // argsLength
                .PushData(0)               // argsOffset
                .PushData(0)               // value
                .PushData(TestItem.AddressC)
                .PushData(UInt256.MaxValue) // gasLimit — high bits set; cap clamps it
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 200_000, code);
            Assert.That(result.Error, Is.Null, "CALL must succeed: 63/64 cap clamps gasLimit to a long-representable value");
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        }
    }

    /// <summary>
    /// Pre-EIP-150 (Frontier / Homestead) CALL behaviour for caller-supplied <c>gasLimit</c>
    /// values larger than <see cref="long.MaxValue"/>. With the 63/64 rule inactive, the only
    /// guard before <c>(long)gasLimit</c> is the explicit <c>gasLimit &gt;= long.MaxValue</c>
    /// OOG check; without it, the UInt256→long cast throws <c>OverflowException</c> instead of
    /// the EVM-correct <c>OutOfGas</c>. This locks the guard in place against future refactors.
    /// </summary>
    public class CallTestsPreEip150 : VirtualMachineTestsBase
    {
        protected override long BlockNumber => 0;
        protected override ulong Timestamp => 0;

        // DELEGATECALL and STATICCALL are not yet available at this fork (Homestead / Byzantium),
        // so the pre-EIP-150 guard can only be exercised through CALL and CALLCODE here.
        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        public void Call_with_gas_above_long_max_out_of_gas(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)               // retLength
                .PushData(0)               // retOffset
                .PushData(0)               // argsLength
                .PushData(0)               // argsOffset
                .PushData(0)               // value
                .PushData(TestItem.AddressC)
                .PushData(UInt256.MaxValue) // gasLimit — high bits set, exceeds long.MaxValue
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 200_000, code);
            Assert.That(result.Error, Is.EqualTo("OutOfGas"));
        }
    }
}
