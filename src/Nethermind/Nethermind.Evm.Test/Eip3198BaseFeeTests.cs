// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip3198BaseFeeTests : VirtualMachineTestsBase
    {

        [TestCase(true, 0UL, true)]
        [TestCase(true, 100UL, true)]
        [TestCase(true, 20UL, true)]
        [TestCase(false, 20UL, true)]
        [TestCase(false, 0UL, true)]
        [TestCase(true, 0UL, false)]
        [TestCase(true, 100UL, false)]
        [TestCase(true, 20UL, false)]
        [TestCase(false, 20UL, false)]
        [TestCase(false, 0UL, false)]
        public void Base_fee_opcode_should_return_expected_results(bool eip3198Enabled, ulong baseFee, bool send1559Tx)
        {
            _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);
            byte[] code = Prepare.EvmCode
                .Op(Instruction.BASEFEE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            ulong blockNumber = eip3198Enabled ? MainnetSpecProvider.LondonBlockNumber : MainnetSpecProvider.LondonBlockNumber - 1;
            (Block block, Transaction transaction) = PrepareTx((blockNumber, 0UL), 100000UL, code);
            block.Header.BaseFeePerGas = baseFee;
            if (send1559Tx)
            {
                transaction.DecodedMaxFeePerGas = baseFee;
                transaction.Type = TxType.EIP1559;
            }
            else
            {
                transaction.GasPrice = baseFee;
            }

            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

            if (eip3198Enabled)
            {
                AssertStorage((UInt256)0, (UInt256)baseFee);
            }
            else
            {
                Assert.That(tracer.Error, Is.EqualTo(EvmExceptionType.BadInstruction.ToString()));
                AssertStorage((UInt256)0, (UInt256)0);
            }
        }
    }
}
