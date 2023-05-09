// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

using Newtonsoft.Json.Linq;

using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    // Vitalik Buterin, Martin Swende, "EIP-3529: Reduction in refunds [DRAFT]," Ethereum Improvement Proposals, no. 3529, April 2021. [Online serial]. Available: https://eips.ethereum.org/EIPS/eip-3529.
    public class Eip3529Tests : VirtualMachineTestsBase
    {

        [TestCase("0x60006000556000600055", 212, 0, 0)]
        [TestCase("0x60006000556001600055", 20112, 0, 0)]
        [TestCase("0x60016000556000600055", 20112, 19900, 0)]
        [TestCase("0x60016000556002600055", 20112, 0, 0)]
        [TestCase("0x60016000556001600055", 20112, 0, 0)]
        [TestCase("0x60006000556000600055", 3012, 15000, 1)]
        [TestCase("0x60006000556001600055", 3012, 2800, 1)]
        [TestCase("0x60006000556002600055", 3012, 0, 1)]
        [TestCase("0x60026000556000600055", 3012, 15000, 1)]
        [TestCase("0x60026000556003600055", 3012, 0, 1)]
        [TestCase("0x60026000556001600055", 3012, 2800, 1)]
        [TestCase("0x60026000556002600055", 3012, 0, 1)]
        [TestCase("0x60016000556000600055", 3012, 15000, 1)]
        [TestCase("0x60016000556002600055", 3012, 0, 1)]
        [TestCase("0x60016000556001600055", 212, 0, 1)]
        [TestCase("0x600160005560006000556001600055", 40118, 19900, 0)]
        [TestCase("0x600060005560016000556000600055", 5918, 17800, 1)]
        public void Before_introducing_eip3529(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            Test(codeHex, gasUsed, refund, originalValue, false);
        }

        [TestCase("0x60006000556000600055", 212, 0, 0)]
        [TestCase("0x60006000556001600055", 20112, 0, 0)]
        [TestCase("0x60016000556000600055", 20112, 19900, 0)]
        [TestCase("0x60016000556002600055", 20112, 0, 0)]
        [TestCase("0x60016000556001600055", 20112, 0, 0)]
        [TestCase("0x60006000556000600055", 3012, 4800, 1)]
        [TestCase("0x60006000556001600055", 3012, 2800, 1)]
        [TestCase("0x60006000556002600055", 3012, 0, 1)]
        [TestCase("0x60026000556000600055", 3012, 4800, 1)]
        [TestCase("0x60026000556003600055", 3012, 0, 1)]
        [TestCase("0x60026000556001600055", 3012, 2800, 1)]
        [TestCase("0x60026000556002600055", 3012, 0, 1)]
        [TestCase("0x60016000556000600055", 3012, 4800, 1)]
        [TestCase("0x60016000556002600055", 3012, 0, 1)]
        [TestCase("0x60016000556001600055", 212, 0, 1)]
        [TestCase("0x600160005560006000556001600055", 40118, 19900, 0)]
        [TestCase("0x600060005560016000556000600055", 5918, 7600, 1)]
        public void After_introducing_eip3529(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            Test(codeHex, gasUsed, refund, originalValue, true);
        }

        private void Test(string codeHex, long gasUsed, long refund, byte originalValue, bool eip3529Enabled)
        {
            TestState.CreateAccount(Recipient, 0);
            Storage.Set(new StorageCell(Recipient, 0), (UInt256)originalValue);
            Storage.Commit();
            TestState.Commit(eip3529Enabled ? London.Instance : Berlin.Instance);
            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, Machine, LimboLogs.Instance);
            long blockNumber = eip3529Enabled ? MainnetSpecProvider.LondonBlockNumber : MainnetSpecProvider.LondonBlockNumber - 1;
            (Block block, Transaction transaction) = PrepareTx(blockNumber, 100000, Bytes.FromHexString(codeHex));

            transaction.GasPrice = 20.GWei();
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);

            Assert.That(tracer.Refund, Is.EqualTo(refund));
            AssertGas(tracer, gasUsed + GasCostOf.Transaction - Math.Min((gasUsed + GasCostOf.Transaction) / (eip3529Enabled ? RefundHelper.MaxRefundQuotientEIP3529 : RefundHelper.MaxRefundQuotient), refund));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void After_3529_self_destruct_has_zero_refund(bool eip3529Enabled)
        {
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);

            byte[] baseInitCodeStore = Prepare.EvmCode
                .PushData(2)
                .PushData(2)
                .Op(Instruction.SSTORE).Done;

            byte[] baseInitCodeAfterStore = Prepare.EvmCode
                .ForInitOf(
                    Prepare.EvmCode
                        .PushData(1)
                        .Op(Instruction.SLOAD)
                        .PushData(1)
                        .Op(Instruction.EQ)
                        .PushData(17)
                        .Op(Instruction.JUMPI)
                        .PushData(1)
                        .PushData(1)
                        .Op(Instruction.SSTORE)
                        .PushData(21)
                        .Op(Instruction.JUMP)
                        .Op(Instruction.JUMPDEST)
                        .PushData(0)
                        .Op(Instruction.SELFDESTRUCT)
                        .Op(Instruction.JUMPDEST)
                        .Done)
                .Done;

            byte[] baseInitCode = Bytes.Concat(baseInitCodeStore, baseInitCodeAfterStore);

            byte[] create2Code = Prepare.EvmCode
                .ForCreate2Of(baseInitCode)
                .Done;

            byte[] initOfCreate2Code = Prepare.EvmCode
                .ForInitOf(create2Code)
                .Done;

            Address deployingContractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            Address deploymentAddress = ContractAddress.From(deployingContractAddress, new byte[32], baseInitCode);

            byte[] deploy = Prepare.EvmCode
                .Call(deployingContractAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode1 = Prepare.EvmCode
                .Call(deploymentAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode2 = Prepare.EvmCode
                .Call(deploymentAddress, 100000)
                .Op(Instruction.STOP).Done;

            long gasLimit = 1000000;

            EthereumEcdsa ecdsa = new(1, LimboLogs.Instance);
            // deploy create 2
            Transaction tx0 = Build.A.Transaction.WithCode(initOfCreate2Code).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // invoke create 2 to deploy contract
            Transaction tx1 = Build.A.Transaction.WithCode(deploy).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call contract once
            Transaction tx2 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // self destruct contract
            Transaction tx3 = Build.A.Transaction.WithCode(byteCode2).WithGasLimit(gasLimit).WithNonce(3).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            int gasUsedByTx3 = 37767;

            long blockNumber = eip3529Enabled ? MainnetSpecProvider.LondonBlockNumber : MainnetSpecProvider.LondonBlockNumber - 1;
            Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(tx0, tx1, tx2, tx3).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer tracer0 = new(block, tx0, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx0, block.Header, tracer0);

            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(tx1, block.Header, tracer);

            tracer = CreateTracer();
            _processor.Execute(tx2, block.Header, tracer);

            tracer = CreateTracer();
            _processor.Execute(tx3, block.Header, tracer);
            long expectedRefund = eip3529Enabled ? 0 : 24000;

            Assert.That(tracer.Refund, Is.EqualTo(expectedRefund));
            AssertGas(tracer, gasUsedByTx3 + GasCostOf.Transaction - Math.Min((gasUsedByTx3 + GasCostOf.Transaction) / (eip3529Enabled ? RefundHelper.MaxRefundQuotientEIP3529 : RefundHelper.MaxRefundQuotient), expectedRefund));
        }
    }
}
