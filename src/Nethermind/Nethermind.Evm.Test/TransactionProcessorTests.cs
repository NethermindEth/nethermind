//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Buffers.Binary;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    [Todo(Improve.Refactor, "Check why fixture test cases did not work")]
    public class TransactionProcessorTests
    {
        private ISpecProvider _specProvider;
        private IEthereumEcdsa _ethereumEcdsa;
        private TransactionProcessor _transactionProcessor;
        private StateProvider _stateProvider;

        [SetUp]
        public void Setup()
        {
            _specProvider = MainNetSpecProvider.Instance;
            StateDb stateDb = new StateDb();
            _stateProvider = new StateProvider(stateDb, new MemDb(), LimboLogs.Instance);
            _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
            _stateProvider.Commit(_specProvider.GenesisSpec);
            _stateProvider.CommitTree();

            StorageProvider storageProvider = new StorageProvider(stateDb, _stateProvider, LimboLogs.Instance);
            VirtualMachine virtualMachine = new VirtualMachine(_stateProvider, storageProvider, Substitute.For<IBlockhashProvider>(), _specProvider, LimboLogs.Instance);
            _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);
            _ethereumEcdsa = new EthereumEcdsa(_specProvider, LimboLogs.Instance);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_process_simple_transaction(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(100000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            Assert.AreEqual(StatusCode.Success, tracer.TxReceipts[0].StatusCode);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Sets_state_root_on_receipts_before_eip658(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(100000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            Assert.NotNull(tracer.TxReceipts[0].PostTransactionState);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_handle_quick_fail_on_intrinsic_gas(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(20000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            Assert.AreEqual(StatusCode.Failure, tracer.TxReceipts[0].StatusCode);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_handle_quick_fail_on_missing_sender(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.Signed(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(100000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            Assert.AreEqual(StatusCode.Failure, tracer.TxReceipts[0].StatusCode);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_handle_quick_fail_on_non_existing_sender_account(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB, 1).WithGasLimit(100000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            Assert.AreEqual(StatusCode.Failure, tracer.TxReceipts[0].StatusCode);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_handle_quick_fail_on_invalid_nonce(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(100000).WithNonce(100).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            Assert.AreEqual(StatusCode.Failure, tracer.TxReceipts[0].StatusCode);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_handle_quick_fail_on_not_enough_balance(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(100000).TestObject;
            tx.Value = 2.Ether();

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            Assert.AreEqual(StatusCode.Failure, tracer.TxReceipts[0].StatusCode);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_handle_quick_fail_on_above_block_gas_limit(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(100000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(20000).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            Assert.AreEqual(StatusCode.Failure, tracer.TxReceipts[0].StatusCode);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Will_not_cause_quick_fail_above_block_gas_limit_during_calls(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(100000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(20000).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            CallAndRestore(tracer, tx, block);

            Assert.AreEqual(StatusCode.Success, tracer.TxReceipts[0].StatusCode);
        }

        [Test]
        public void Can_estimate_simple()
        {
            long gasLimit = 100000;
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            EstimateGasTracer tracer = new EstimateGasTracer();
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);
            
            Assert.AreEqual(21000, tracer.GasSpent);
            Assert.AreEqual(0, tracer.AdditionalGasRequired);
        }

        [Test]
        public void Can_estimate_with_single_call()
        {
            byte[] initByteCode = Prepare.EvmCode
                .ForInitOf(Bytes.FromHexString("6000")).Done;

            Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

            byte[] byteCode = Prepare.EvmCode
                .Call(contractAddress, 46179).Done;

            long gasLimit = 100000;

            Transaction initTx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithInit(initByteCode).WithGasLimit(gasLimit).TestObject;
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, 1).WithInit(byteCode).WithGasLimit(gasLimit).WithNonce(1).TestObject;
            Block block = Build.A.Block.WithNumber(MainNetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

            IntrinsicGasCalculator gasCalculator = new IntrinsicGasCalculator();
            long intrinsic = gasCalculator.Calculate(tx, MuirGlacier.Instance);

            _transactionProcessor.Execute(initTx, block.Header, NullTxTracer.Instance);

            EstimateGasTracer tracer = new EstimateGasTracer();
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);
            
            long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
            actualIntrinsic.Should().Be(53000);
            tracer.AdditionalGasRequired.Should().Be(1);
            tracer.GasSpent.Should().Be(53724);
            long estimate = tracer.GasSpent + tracer.AdditionalGasRequired;
            estimate.Should().Be(53725);
        }

        [Test]
        public void Disables_Eip158_for_system_transactions()
        {
            _stateProvider.CreateAccount(TestItem.PrivateKeyA.Address, 0.Ether());
            _stateProvider.Commit(_specProvider.GetSpec(1));

            var blockNumber = MainNetSpecProvider.SpuriousDragonBlockNumber + 1;
            Transaction tx = Build.A.SystemTransaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, blockNumber)
                .WithGasPrice(0)
                .WithValue(0)
                .TestObject;

            Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, false, false);
            Execute(tracer, tx, block);
            _stateProvider.AccountExists(tx.SenderAddress).Should().BeTrue();
        }

        private BlockReceiptsTracer BuildTracer(Block block, Transaction tx, bool stateDiff, bool trace)
        {
            ParityTraceTypes types = ParityTraceTypes.None;
            if (stateDiff)
            {
                types = types | ParityTraceTypes.StateDiff;
            }

            if (trace)
            {
                types = types | ParityTraceTypes.Trace;
            }

            IBlockTracer otherTracer = types != ParityTraceTypes.None ? new ParityLikeBlockTracer(tx.Hash, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff) : (IBlockTracer) NullBlockTracer.Instance;
            BlockReceiptsTracer tracer = new BlockReceiptsTracer();
            tracer.SetOtherTracer(otherTracer);
            return tracer;
        }

        private void Execute(BlockReceiptsTracer tracer, Transaction tx, Block block)
        {
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(tx.Hash);
            _transactionProcessor.Execute(tx, block.Header, tracer);
            tracer.EndTxTrace();
        }

        private void CallAndRestore(BlockReceiptsTracer tracer, Transaction tx, Block block)
        {
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(tx.Hash);
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);
            tracer.EndTxTrace();
        }
    }
}