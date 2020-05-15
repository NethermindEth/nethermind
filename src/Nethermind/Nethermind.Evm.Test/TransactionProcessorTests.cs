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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Todo(Improve.Refactor, "Check why fixture test cases did not work")]
    public class TransactionProcessorTests
    {
        private bool _isEip155Enabled;
        private ISpecProvider _specProvider;
        private IEthereumEcdsa _ethereumEcdsa;
        private TransactionProcessor _transactionProcessor;
        private StateProvider _stateProvider;

        public TransactionProcessorTests(bool eip155Enabled)
        {
            _isEip155Enabled = eip155Enabled;
            _specProvider = eip155Enabled ? (ISpecProvider)GoerliSpecProvider.Instance : MainnetSpecProvider.Instance;
        }
        
        [SetUp]
        public void Setup()
        {
            StateDb stateDb = new StateDb();
            _stateProvider = new StateProvider(stateDb, new MemDb(), LimboLogs.Instance);
            _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
            _stateProvider.Commit(_specProvider.GenesisSpec);
            _stateProvider.CommitTree();

            StorageProvider storageProvider = new StorageProvider(stateDb, _stateProvider, LimboLogs.Instance);
            VirtualMachine virtualMachine = new VirtualMachine(_stateProvider, storageProvider, Substitute.For<IBlockhashProvider>(), _specProvider, LimboLogs.Instance);
            _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);
            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_process_simple_transaction(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

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
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            Execute(tracer, tx, block);

            if (_isEip155Enabled) // we use eip155 check just as a proxy on 658
            {
                Assert.Null(tracer.TxReceipts[0].PostTransactionState);
            }
            else
            {
                Assert.NotNull(tracer.TxReceipts[0].PostTransactionState);
            }
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void Can_handle_quick_fail_on_intrinsic_gas(bool withStateDiff, bool withTrace)
        {
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(20000).TestObject;

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
            Transaction tx = Build.A.Transaction.Signed(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

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
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB, _isEip155Enabled).WithGasLimit(100000).TestObject;

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
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).WithNonce(100).TestObject;

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
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;
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
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

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
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(20000).TestObject;

            BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
            CallAndRestore(tracer, tx, block);

            Assert.AreEqual(StatusCode.Success, tracer.TxReceipts[0].StatusCode);
        }

        [Test]
        public void Balance_is_not_changed_on_call_and_restore()
        {
            long gasLimit = 100000;
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(1.Ether() - (UInt256)gasLimit).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            _transactionProcessor.CallAndRestore(tx, block.Header, NullTxTracer.Instance);

            _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(1.Ether());
        }
        
        [Test]
        public void Account_is_not_created_on_call_and_restore()
        {
            long gasLimit = 100000;
            Transaction tx = Build.A.Transaction
                .WithValue(0.Ether())
                .WithGasPrice(1)
                .WithGasLimit(gasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyD, _isEip155Enabled)
                .TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeFalse();
            _transactionProcessor.CallAndRestore(tx, block.Header, NullTxTracer.Instance);
            _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeFalse();
        }
        
        [Test]
        public void Nonce_is_not_changed_on_call_and_restore()
        {
            long gasLimit = 100000;
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(1.Ether() - (UInt256)gasLimit).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            _transactionProcessor.CallAndRestore(tx, block.Header, NullTxTracer.Instance);
            _stateProvider.GetNonce(TestItem.PrivateKeyA.Address).Should().Be(0);
        }

        
        [Test]
        public void Can_estimate_simple()
        {
            long gasLimit = 100000;
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

            EstimateGasTracer tracer = new EstimateGasTracer();
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

            tracer.GasSpent.Should().Be(21000);
            tracer.CalculateEstimate(tx).Should().Be(21000);
        }

        [Test]
        public void Can_estimate_with_refund()
        {
            byte[] initByteCode = Prepare.EvmCode
                .PushData(1)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;

            long gasLimit = 100000;

            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithInit(initByteCode).WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

            IntrinsicGasCalculator gasCalculator = new IntrinsicGasCalculator();
            long intrinsic = gasCalculator.Calculate(tx, MuirGlacier.Instance);

            GethLikeTxTracer gethTracer = new GethLikeTxTracer(GethTraceOptions.Default);
            _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
            TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

            EstimateGasTracer tracer = new EstimateGasTracer();
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

            long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
            actualIntrinsic.Should().Be(intrinsic);
            tracer.CalculateAdditionalGasRequired(tx).Should().Be(RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend - GasCostOf.SStoreNetMeteredEip2200 + 1);
            tracer.GasSpent.Should().Be(54764L);
            long estimate = tracer.CalculateEstimate(tx);
            estimate.Should().Be(75465L);

            ConfirmEnoughEstimate(tx, block, estimate);
        }

        [Test(Description = "Since the second call is a CREATE operation it has intrinsic gas of 21000 + 32000 + data")]
        public void Can_estimate_with_destroy_refund_and_below_intrinsic()
        {
            byte[] initByteCode = Prepare.EvmCode.ForInitOf(Prepare.EvmCode.PushData(Address.Zero).Op(Instruction.SELFDESTRUCT).Done).Done;
            Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

            byte[] byteCode = Prepare.EvmCode
                .Call(contractAddress, 46179)
                .Op(Instruction.STOP).Done;

            long gasLimit = 100000;

            Transaction initTx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithInit(initByteCode).WithGasLimit(gasLimit).TestObject;
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithInit(byteCode).WithGasLimit(gasLimit).WithNonce(1).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

            IntrinsicGasCalculator gasCalculator = new IntrinsicGasCalculator();
            long intrinsic = gasCalculator.Calculate(tx, MuirGlacier.Instance);

            _transactionProcessor.Execute(initTx, block.Header, NullTxTracer.Instance);

            EstimateGasTracer tracer = new EstimateGasTracer();
            GethLikeTxTracer gethTracer = new GethLikeTxTracer(GethTraceOptions.Default);
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);
            _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
            TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

            long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
            actualIntrinsic.Should().Be(intrinsic);
            tracer.CalculateAdditionalGasRequired(tx).Should().Be(24080);
            tracer.GasSpent.Should().Be(35228L);
            long estimate = tracer.CalculateEstimate(tx);
            estimate.Should().Be(59308);

            ConfirmEnoughEstimate(tx, block, estimate);
        }

        private void ConfirmEnoughEstimate(Transaction tx, Block block, long estimate)
        {
            CallOutputTracer outputTracer = new CallOutputTracer();
            tx.GasLimit = estimate;
            TestContext.WriteLine(tx.GasLimit);

            GethLikeTxTracer gethTracer = new GethLikeTxTracer(GethTraceOptions.Default);
            _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
            string traceEnoughGas = new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true);

            _transactionProcessor.CallAndRestore(tx, block.Header, outputTracer);
            traceEnoughGas.Should().NotContain("OutOfGas");

            outputTracer = new CallOutputTracer();
            tx.GasLimit = Math.Min(estimate - 1, estimate * 63 / 64);
            TestContext.WriteLine(tx.GasLimit);
            
            gethTracer = new GethLikeTxTracer(GethTraceOptions.Default);
            _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);

            string traceOutOfGas = new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true);
            TestContext.WriteLine(traceOutOfGas);
            
            _transactionProcessor.CallAndRestore(tx, block.Header, outputTracer);

            bool failed = traceEnoughGas.Contains("failed") || traceEnoughGas.Contains("OutOfGas");
            failed.Should().BeTrue();
        }

        [Test]
        public void Can_estimate_with_stipend()
        {
            byte[] initByteCode = Prepare.EvmCode
                .CallWithValue(Address.Zero, 0, 1)
                .Op(Instruction.STOP)
                .Done;

            long gasLimit = 100000;

            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithInit(initByteCode).WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

            IntrinsicGasCalculator gasCalculator = new IntrinsicGasCalculator();
            long intrinsic = gasCalculator.Calculate(tx, MuirGlacier.Instance);

            GethLikeTxTracer gethTracer = new GethLikeTxTracer(GethTraceOptions.Default);
            _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
            TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

            EstimateGasTracer tracer = new EstimateGasTracer();
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

            long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
            actualIntrinsic.Should().Be(intrinsic);
            tracer.CalculateAdditionalGasRequired(tx).Should().Be(2300);
            tracer.GasSpent.Should().Be(85669L);
            long estimate = tracer.CalculateEstimate(tx);
            estimate.Should().Be(87969L);

            ConfirmEnoughEstimate(tx, block, estimate);
        }
        
        [Test]
        public void Can_estimate_with_stipend_and_refund()
        {
            byte[] initByteCode = Prepare.EvmCode
                .CallWithValue(Address.Zero, 0, 1)
                .PushData(1)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;

            long gasLimit = 200000;

            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithInit(initByteCode).WithGasLimit(gasLimit).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

            IntrinsicGasCalculator gasCalculator = new IntrinsicGasCalculator();
            long intrinsic = gasCalculator.Calculate(tx, MuirGlacier.Instance);

            GethLikeTxTracer gethTracer = new GethLikeTxTracer(GethTraceOptions.Default);
            _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
            TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

            EstimateGasTracer tracer = new EstimateGasTracer();
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

            long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
            actualIntrinsic.Should().Be(intrinsic);
            tracer.CalculateAdditionalGasRequired(tx).Should().Be(RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend);
            tracer.GasSpent.Should().Be(87429L);
            long estimate = tracer.CalculateEstimate(tx);
            estimate.Should().Be(87429L + RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend);

            ConfirmEnoughEstimate(tx, block, estimate);
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

            Transaction initTx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithInit(initByteCode).WithGasLimit(gasLimit).TestObject;
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithInit(byteCode).WithGasLimit(gasLimit).WithNonce(1).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

            IntrinsicGasCalculator gasCalculator = new IntrinsicGasCalculator();
            long intrinsic = gasCalculator.Calculate(tx, MuirGlacier.Instance);

            _transactionProcessor.Execute(initTx, block.Header, NullTxTracer.Instance);

            EstimateGasTracer tracer = new EstimateGasTracer();
            _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

            long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
            actualIntrinsic.Should().Be(intrinsic);
            tracer.CalculateAdditionalGasRequired(tx).Should().Be(1);
            tracer.GasSpent.Should().Be(54224L);
            long estimate = tracer.CalculateEstimate(tx);
            estimate.Should().Be(54225L);

            ConfirmEnoughEstimate(tx, block, estimate);
        }

        [Test]
        public void Disables_Eip158_for_system_transactions()
        {
            var blockNumber = MainnetSpecProvider.SpuriousDragonBlockNumber + 1;
            _stateProvider.CreateAccount(TestItem.PrivateKeyA.Address, 0.Ether());
            IReleaseSpec spec = _specProvider.GetSpec(blockNumber);
            _stateProvider.Commit(spec);
            Transaction tx = Build.A.SystemTransaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
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