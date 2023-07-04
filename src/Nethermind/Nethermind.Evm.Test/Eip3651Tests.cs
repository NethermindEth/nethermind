// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Core;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class Eip3651Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.ShanghaiBlockTimestamp;

        [Test]
        public void Access_beneficiary_address_after_eip_3651()
        {
            byte[] code = Prepare.EvmCode
                .PushData(MinerKey.Address)
                .Op(Instruction.BALANCE)
                .Op(Instruction.POP)
                .Done;
            TestAllTracerWithOutput result = Execute(code);
            result.StatusCode.Should().Be(1);
            AssertGas(result, GasCostOf.Transaction + 105);
        }

        [Test]
        public void Access_beneficiary_address_before_eip_3651()
        {
            TestState.CreateAccount(TestItem.AddressF, 100.Ether());
            byte[] code = Prepare.EvmCode
                .PushData(MinerKey.Address)
                .Op(Instruction.BALANCE)
                .Op(Instruction.POP)
                .Done;
            TestAllTracerWithOutput result = Execute(BlockNumber, 100000, code, timestamp: Timestamp - 1);
            result.StatusCode.Should().Be(1);
            AssertGas(result, GasCostOf.Transaction + 2605);
        }

        protected override TestAllTracerWithOutput CreateTracer()
        {
            TestAllTracerWithOutput tracer = base.CreateTracer();
            tracer.IsTracingAccess = false;
            return tracer;
        }

    }
}
