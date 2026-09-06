// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class SignExtTests : VirtualMachineTestsBase
    {
        [TestCase(0, 0, Description = "Sign extend zero")]
        [TestCase(255, -1, Description = "Sign extend max")]
        public void Sign_ext_value(int value, int expectedResult)
        {
            UInt256 expected = expectedResult == -1 ? UInt256.MaxValue : (UInt256)expectedResult;
            byte[] code = Prepare.EvmCode
                .PushData(value)
                .PushData(0)
                .Op(Instruction.SIGNEXTEND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, expected);
        }

        [Test]
        public void Sign_ext_underflow()
        {
            byte[] code = Prepare.EvmCode
                .PushData(32)
                .Op(Instruction.SIGNEXTEND)
                .Done;

            TestAllTracerWithOutput res = Execute(code);
            Assert.That(res.Error, Is.EqualTo(EvmExceptionType.StackUnderflow.ToString()));
        }

        [Test]
        public void Sign_ext_fills_above_a_negative_sign_byte([Range(0, 31)] int byteIndex)
        {
            // Only the sign bit of the selected byte is set, so the result is that bit and every bit above.
            UInt256 value = UInt256.One << (8 * byteIndex + 7);
            UInt256 expected = UInt256.MaxValue - (value - UInt256.One);

            AssertSignExtend(byteIndex, value, expected);
        }

        [Test]
        public void Sign_ext_clears_above_a_positive_sign_byte([Range(0, 30)] int byteIndex)
        {
            // The byte above the sign byte is set while the sign byte itself is clear.
            UInt256 value = (UInt256)0xff << (8 * (byteIndex + 1));

            AssertSignExtend(byteIndex, value, UInt256.Zero);
        }

        [Test]
        public void Sign_ext_of_the_most_significant_byte_changes_nothing()
        {
            // Index 31 puts the sign byte at the top of the word, so no byte sits above it to rewrite.
            UInt256 value = (UInt256)0x7f << 248;

            AssertSignExtend(31, value, value);
        }

        [TestCase(32, Description = "First out-of-range index")]
        [TestCase(255, Description = "Well past the word")]
        public void Sign_ext_out_of_range_index_changes_nothing(int byteIndex)
        {
            UInt256 value = (UInt256)0xff << 248;

            AssertSignExtend(byteIndex, value, value);
        }

        // Every index here reads as zero in its last byte, which would extend from byte 31.
        private static IEnumerable<UInt256> IndicesAboveTheLastByte()
        {
            yield return 256;
            yield return UInt256.One << 64;
            yield return UInt256.One << 128;
            yield return UInt256.One << 192;
        }

        [TestCaseSource(nameof(IndicesAboveTheLastByte))]
        public void Sign_ext_index_above_the_last_byte_changes_nothing(UInt256 byteIndex)
        {
            // An index is in range only when the whole 256-bit word is below 32, not just its last byte.
            UInt256 value = 0xff;

            AssertSignExtend(byteIndex, value, value);
        }

        private void AssertSignExtend(int byteIndex, UInt256 value, UInt256 expected)
            => AssertSignExtend((UInt256)byteIndex, value, expected);

        // The index is pushed last because SIGNEXTEND pops it before peeking the value.
        private void AssertSignExtend(UInt256 byteIndex, UInt256 value, UInt256 expected)
        {
            byte[] code = Prepare.EvmCode
                .PushData(value)
                .PushData(byteIndex)
                .Op(Instruction.SIGNEXTEND)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            _ = Execute(code);
            AssertStorage(UInt256.Zero, expected);
        }
    }
}
