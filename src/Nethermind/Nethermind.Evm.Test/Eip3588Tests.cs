// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;
using FluentAssertions.Execution;
using Nethermind.Core.Specs;
using NSubstitute;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip3855Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.ShanghaiBlockTimestamp;

        private TestAllTracerWithOutput testBase(int repeat, bool isShanghai)
        {
            ulong timestampParam = isShanghai ? Timestamp : Timestamp - 1;
            Prepare codeInitializer = Prepare.EvmCode;
            for (int i = 0; i < repeat; i++)
            {
                codeInitializer.Op(Instruction.PUSH0);
            }

            byte[] code = codeInitializer.Done;
            TestAllTracerWithOutput receipt = Execute(BlockNumber, 1_000_000, code, timestamp: timestampParam);
            return receipt;
        }

        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(123, true)]
        [TestCase(1024, true)]
        public void Test_Eip3855_should_pass(int repeat, bool isShanghai)
        {
            TestAllTracerWithOutput receipt = testBase(repeat, isShanghai);
            receipt.StatusCode.Should().Be(StatusCode.Success);
            receipt.GasSpent.Should().Be(repeat * GasCostOf.Base + GasCostOf.Transaction);
        }


        [TestCase(1, false, Description = "Shanghai fork deactivated")]
        [TestCase(123, false, Description = "Shanghai fork deactivated")]
        [TestCase(1234, false, Description = "Shanghai fork deactivated")]
        [TestCase(1025, true, Description = "Shanghai fork activated, stackoverflow")]
        [TestCase(1026, true, Description = "Shanghai fork activated, stackoverflow")]
        public void Test_Eip3855_should_fail(int repeat, bool isShanghai)
        {
            TestAllTracerWithOutput receipt = testBase(repeat, isShanghai);

            receipt.StatusCode.Should().Be(StatusCode.Failure);

            if (isShanghai && repeat > 1024) // should fail because of stackoverflow (exceeds stack limit of 1024)
            {
                receipt.Error.Should().Be(EvmExceptionType.StackOverflow.ToString());
            }

            if (!isShanghai) // should fail because of bad instruction (push zero is an EIP-3540 new instruction)
            {
                receipt.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
            }
        }
    }
}
