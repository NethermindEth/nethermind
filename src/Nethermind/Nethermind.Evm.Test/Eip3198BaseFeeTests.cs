// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip3198BaseFeeTests : VirtualMachineTestsBase
    {

        [TestCase(true, 0, true)]
        [TestCase(true, 100, true)]
        [TestCase(true, 20, true)]
        [TestCase(false, 20, true)]
        [TestCase(false, 0, true)]
        [TestCase(true, 0, false)]
        [TestCase(true, 100, false)]
        [TestCase(true, 20, false)]
        [TestCase(false, 20, false)]
        [TestCase(false, 0, false)]
        public void Base_fee_opcode_should_return_expected_results(bool eip3198Enabled, int baseFee, bool send1559Tx)
        {
            _processor = new TransactionProcessor(SpecProvider, TestState, Machine, LimboLogs.Instance);
            byte[] code = Prepare.EvmCode
                .Op(Instruction.BASEFEE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            long blockNumber = eip3198Enabled ? MainnetSpecProvider.LondonBlockNumber : MainnetSpecProvider.LondonBlockNumber - 1;
            (Block block, Transaction transaction) = PrepareTx((blockNumber, 0), 100000, code);
            block.Header.BaseFeePerGas = (UInt256)baseFee;
            if (send1559Tx)
            {
                transaction.DecodedMaxFeePerGas = (UInt256)baseFee;
                transaction.Type = TxType.EIP1559;
            }
            else
            {
                transaction.GasPrice = (UInt256)baseFee;
            }

            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);

            if (eip3198Enabled)
            {
                AssertStorage((UInt256)0, (UInt256)baseFee);
            }
            else
            {
                tracer.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
                AssertStorage((UInt256)0, (UInt256)0);
            }
        }
    }
}
