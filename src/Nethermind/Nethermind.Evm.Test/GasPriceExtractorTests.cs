// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [Explicit("Failing on MacOS GitHub Actions with stack overflow")]
    [TestFixture]
    public class GasPriceExtractorTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;

        [Test]
        public void Block_header_rlp_size_assumption_is_correct()
        {
            Rlp rlp = BuildHeader();

            rlp.Bytes.Length.Should().BeLessThan(600);
        }

        [Test]
        public void Intrinsic_gas_cost_assumption_is_correct()
        {
            Rlp rlp = BuildHeader();

            Transaction tx = Build.A.Transaction.WithData(rlp.Bytes).TestObject;
            long gasCost = IntrinsicGasCalculator.Calculate(tx, Spec);
            gasCost.Should().BeLessThan(21000 + 9600);
        }

        [Test]
        public void Keccak_gas_cost_assumption_is_correct()
        {
            Rlp rlp = BuildHeader();

            Transaction tx = Build.A.Transaction.WithData(rlp.Bytes).TestObject;
            long gasCost = IntrinsicGasCalculator.Calculate(tx, Spec);
            gasCost.Should().BeLessThan(21000 + 9600);

            byte[] bytecode =
                Prepare.EvmCode
                    .PushData("0x0200")
                    .PushData(0)
                    .PushData(0)
                    .Op(Instruction.CALLDATACOPY)
                    .PushData("0x0200")
                    .PushData(0)
                    .Op(Instruction.SHA3)
                    .Done;

            (Block block, Transaction transaction) = PrepareTx(
                BlockNumber, 1000000, bytecode, rlp.Bytes, 0);

            CallOutputTracer callOutputTracer = new();
            _processor.Execute(transaction, block.Header, callOutputTracer);
            long minorCostsEstimate = 100;
            long keccakCostEstimate = 30 + 512 / 6;
            callOutputTracer.GasSpent.Should().BeLessThan(21000 + 9600 + minorCostsEstimate + keccakCostEstimate);
        }

        [Test]
        public void Blockhash_times_256_gas_cost_assumption_is_correct()
        {
            Rlp rlp = BuildHeader();
            byte[] bytecode =
                Prepare.EvmCode
                    .PushData(256)
                    .Op(Instruction.JUMPDEST)
                    .PushData(1)
                    .Op(Instruction.DUP2)
                    .Op(Instruction.SUB)
                    .Op(Instruction.DUP1)
                    .Op(Instruction.BLOCKHASH)
                    .Op(Instruction.POP)
                    .PushData(0)
                    .Op(Instruction.DUP2)
                    .Op(Instruction.GT)
                    .PushData(3)
                    .Op(Instruction.JUMPI)
                    .Op(Instruction.STOP)
                    .Done;

            (Block block, Transaction transaction) = PrepareTx(
                BlockNumber, 1000000, bytecode, rlp.Bytes, 0);

            CallOutputTracer callOutputTracer = new();
            _processor.Execute(transaction, block.Header, callOutputTracer);
            callOutputTracer.GasSpent.Should().BeLessThan(21000 + 9600 + 20000);
        }

        [Test]
        public void Blockhash_times_256_no_loop()
        {
            Rlp rlp = BuildHeader();
            Prepare bytecodeBuilder = Prepare.EvmCode
                    .PushData(0)
                    .PushData(1);

            for (int i = 0; i < 256; i++)
            {
                bytecodeBuilder.Op(Instruction.ADD)
                    .Op(Instruction.BLOCKHASH)
                    .PushData(1);
            }

            byte[] bytecode = bytecodeBuilder.Done;

            (Block block, Transaction transaction) = PrepareTx(
                BlockNumber, 1000000, bytecode, rlp.Bytes, 0);

            CallOutputTracer callOutputTracer = new();
            _processor.Execute(transaction, block.Header, callOutputTracer);
            callOutputTracer.GasSpent.Should().BeLessThan(21000 + 9600 + 20000);
        }

        private static Rlp BuildHeader()
        {
            HeaderDecoder decoder = new();
            BlockHeader blockHeader = Build.A.BlockHeader
                .WithBloom(new Bloom(Enumerable.Repeat((byte)1, 256).ToArray())).TestObject;
            Rlp rlp = decoder.Encode(blockHeader);
            return rlp;
        }
    }
}
