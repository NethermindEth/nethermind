// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.TxPool.Comparison;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.State;

namespace Nethermind.Blockchain.Test
{
    [Parallelizable(ParallelScope.All)]
    public class TransactionsExecutorTests
    {
        public static IEnumerable ProperTransactionsSelectedTestCases
        {
            get
            {
                TransactionSelectorTests.ProperTransactionsSelectedTestCase noneTransactionSelectedDueToValue =
                    TransactionSelectorTests.ProperTransactionsSelectedTestCase.Default;
                noneTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 901);
                yield return new TestCaseData(noneTransactionSelectedDueToValue).SetName(
                    "None transactions selected due to value");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase noneTransactionsSelectedDueToGasPrice =
                    TransactionSelectorTests.ProperTransactionsSelectedTestCase.Default;
                noneTransactionsSelectedDueToGasPrice.Transactions.ForEach(t => t.GasPrice = 100);
                yield return new TestCaseData(noneTransactionsSelectedDueToGasPrice).SetName(
                    "None transactions selected due to transaction gas price and limit");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase oneTransactionSelectedDueToValue =
                    TransactionSelectorTests.ProperTransactionsSelectedTestCase.Default;
                oneTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 500);
                oneTransactionSelectedDueToValue.ExpectedSelectedTransactions.AddRange(oneTransactionSelectedDueToValue
                    .Transactions.OrderBy(t => t.Nonce).Take(1));
                yield return new TestCaseData(oneTransactionSelectedDueToValue).SetName(
                    "One transaction selected due to gas limit and value");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase twoTransactionSelectedDueToValue =
                    TransactionSelectorTests.ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 400);
                twoTransactionSelectedDueToValue.ExpectedSelectedTransactions.AddRange(twoTransactionSelectedDueToValue
                    .Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToValue).SetName(
                    "Two transaction selected due to gas limit and value");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase twoTransactionSelectedDueToMinGasPriceForMining =
                    TransactionSelectorTests.ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToMinGasPriceForMining.MinGasPriceForMining = 2;
                twoTransactionSelectedDueToMinGasPriceForMining.ExpectedSelectedTransactions.AddRange(
                    twoTransactionSelectedDueToValue.Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToValue).SetName(
                    "Two transaction selected due to min gas price for mining");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase twoTransactionSelectedDueToWrongNonce =
                    TransactionSelectorTests.ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToWrongNonce.Transactions.First().Nonce = 4;
                twoTransactionSelectedDueToWrongNonce.ExpectedSelectedTransactions.AddRange(
                    twoTransactionSelectedDueToWrongNonce.Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToWrongNonce).SetName(
                    "Two transaction selected due to wrong nonce");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase missingAddressState = TransactionSelectorTests.ProperTransactionsSelectedTestCase.Default;
                missingAddressState.MissingAddresses.Add(TestItem.AddressA);
                yield return new TestCaseData(missingAddressState).SetName("Missing address state");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase complexCase = new()
                {
                    ReleaseSpec = Berlin.Instance,
                    AccountStates =
                    {
                        {TestItem.AddressA, (1000, 1)},
                        {TestItem.AddressB, (1000, 0)},
                        {TestItem.AddressC, (1000, 3)}
                    },
                    Transactions =
                    {
                        // A
                        /*0*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1).WithValue(10)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        /*1*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(3).WithValue(1)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        /*2*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).WithValue(10)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,

                        //B
                        /*3*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(0).WithValue(1)
                            .WithGasPrice(10).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
                        /*4*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(1).WithValue(1)
                            .WithGasPrice(10).WithGasLimit(9).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
                        /*5*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(3).WithValue(1)
                            .WithGasPrice(10).WithGasLimit(9).SignedAndResolved(TestItem.PrivateKeyB).TestObject,

                        //C
                        /*6*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressC).WithNonce(3).WithValue(500)
                            .WithGasPrice(19).WithGasLimit(9).SignedAndResolved(TestItem.PrivateKeyC).TestObject,
                        /*7*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressC).WithNonce(3).WithValue(500)
                            .WithGasPrice(20).WithGasLimit(9).SignedAndResolved(TestItem.PrivateKeyC).TestObject,
                        /*8*/
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressC).WithNonce(4).WithValue(500)
                            .WithGasPrice(20).WithGasLimit(9).SignedAndResolved(TestItem.PrivateKeyC).TestObject,
                    },
                    GasLimit = 10000000
                };
                complexCase.ExpectedSelectedTransactions.AddRange(
                    new[] { 7, 3, 4, 0, 2, 1 }.Select(i => complexCase.Transactions[i]));
                yield return new TestCaseData(complexCase).SetName("Complex case");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase baseFeeBalanceCheck = new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (1000, 1) } },
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(3)
                            .WithGasPrice(60).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1)
                            .WithGasPrice(30).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2)
                            .WithGasPrice(20).WithGasLimit(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject
                    },
                    GasLimit = 10000000
                };
                baseFeeBalanceCheck.ExpectedSelectedTransactions.AddRange(
                    new[] { 1, 2 }.Select(i => baseFeeBalanceCheck.Transactions[i]));
                yield return new TestCaseData(baseFeeBalanceCheck).SetName("Legacy transactions: two transactions selected because of account balance");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase balanceBelowMaxFeeTimesGasLimit = new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (400, 1) } },
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1)
                            .WithMaxFeePerGas(45).WithMaxPriorityFeePerGas(25).WithGasLimit(10).WithType(TxType.EIP1559).WithValue(60).SignedAndResolved(TestItem.PrivateKeyA).TestObject
                    },
                    GasLimit = 10000000
                };
                yield return new TestCaseData(balanceBelowMaxFeeTimesGasLimit).SetName("EIP1559 transactions: none transactions selected because balance is lower than MaxFeePerGas times GasLimit");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase balanceFailingWithMaxFeePerGasCheck =
                    new()
                    {
                        ReleaseSpec = London.Instance,
                        BaseFee = 5,
                        AccountStates = { { TestItem.AddressA, (400, 1) } },
                        Transactions =
                        {
                            Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1)
                                .WithMaxFeePerGas(300).WithMaxPriorityFeePerGas(10).WithGasLimit(10)
                                .WithType(TxType.EIP1559).WithValue(101).SignedAndResolved(TestItem.PrivateKeyA)
                                .TestObject,
                        },
                        GasLimit = 10000000
                    };
                yield return new TestCaseData(balanceFailingWithMaxFeePerGasCheck).SetName("EIP1559 transactions: None transactions selected - sender balance and max fee per gas check");
            }
        }



        public static IEnumerable EIP3860TestCases
        {
            get
            {
                byte[] initCodeBelowTheLimit = Enumerable.Repeat((byte)0x20, (int)Shanghai.Instance.MaxInitCodeSize).ToArray();
                byte[] initCodeAboveTheLimit = Enumerable.Repeat((byte)0x20, (int)Shanghai.Instance.MaxInitCodeSize + 1).ToArray();
                byte[] sigData = new byte[65];
                sigData[31] = 1; // correct r
                sigData[63] = 1; // correct s
                sigData[64] = 27;
                Signature signature = new(sigData);
                Transaction txAboveTheLimit = Build.A.Transaction
                    .WithSignature(signature)
                    .WithGasLimit(10000000)
                    .WithMaxFeePerGas(100.GWei)
                    .WithGasPrice(100.GWei)
                    .WithNonce(1)
                    .WithChainId(TestBlockchainIds.ChainId)
                    .To(null)
                    .WithData(initCodeAboveTheLimit)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                Transaction txAboveTheLimitNoContract = Build.A.Transaction
                    .WithSignature(signature)
                    .WithGasLimit(10000000)
                    .WithMaxFeePerGas(100.GWei)
                    .WithGasPrice(100.GWei)
                    .WithNonce(1)
                    .WithChainId(TestBlockchainIds.ChainId)
                    .To(TestItem.AddressB)
                    .WithData(initCodeAboveTheLimit)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                Transaction txBelowTheLimit = Build.A.Transaction
                    .WithSignature(signature)
                    .WithGasLimit(10000000)
                    .WithMaxFeePerGas(100.GWei)
                    .WithGasPrice(100.GWei)
                    .WithNonce(2)
                    .WithChainId(TestBlockchainIds.ChainId)
                    .To(null)
                    .WithData(initCodeBelowTheLimit)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

                TransactionSelectorTests.ProperTransactionsSelectedTestCase shanghai3860Scenarios = new()
                {
                    ReleaseSpec = Shanghai.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (30000000.Ether, 1) } },
                    Transactions = [txAboveTheLimit, txAboveTheLimitNoContract, txBelowTheLimit],
                    GasLimit = 10000000
                };
                shanghai3860Scenarios.ExpectedSelectedTransactions.AddRange(
                    new[] { 1, 2 }.Select(i => shanghai3860Scenarios.Transactions[i]));
                yield return new TestCaseData(shanghai3860Scenarios).SetName("EIP3860 enabled scenarios");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase london3860Scenarios = new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (30000000.Ether, 1) } },
                    Transactions = [txAboveTheLimit],
                    GasLimit = 10000000
                };
                london3860Scenarios.ExpectedSelectedTransactions.AddRange(
                    new[] { 0 }.Select(i => london3860Scenarios.Transactions[i]));
                yield return new TestCaseData(london3860Scenarios).SetName("EIP3860 disabled scenarios");
            }
        }

        [TestCaseSource(nameof(ProperTransactionsSelectedTestCases))]
        [TestCaseSource(nameof(EIP3860TestCases))]
        public void Proper_transactions_selected(TransactionSelectorTests.ProperTransactionsSelectedTestCase testCase)
        {
            IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
            using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();

            IReleaseSpec spec = testCase.ReleaseSpec;
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            transactionProcessor.When(t => t.BuildUp(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()))
                .Do(info =>
                {
                    Transaction tx = info.Arg<Transaction>();
                    stateProvider.IncrementNonce(tx.SenderAddress!);
                    stateProvider.SubtractFromBalance(tx.SenderAddress!,
                        tx.Value + ((UInt256)tx.GasLimit * tx.GasPrice), spec);
                });

            IBlockTree blockTree = Substitute.For<IBlockTree>();

            TransactionComparerProvider transactionComparerProvider = new(specProvider, blockTree);
            IComparer<Transaction> defaultComparer = transactionComparerProvider.GetDefaultComparer();
            IComparer<Transaction> comparer = CompareTxByNonce.Instance.ThenBy(defaultComparer);
            Transaction[] txArray = testCase.Transactions.Where(t => t?.SenderAddress is not null).OrderBy(t => t, comparer).ToArray();

            Block block = Build.A.Block
                .WithNumber(0)
                .WithBaseFeePerGas(testCase.BaseFee)
                .WithGasLimit(testCase.GasLimit)
                .WithTransactions(txArray)
                .TestObject;
            BlockToProduce blockToProduce = new(block.Header, block.Transactions, block.Uncles);
            blockTree.Head.Returns(blockToProduce);

            void SetAccountStates(IEnumerable<Address> missingAddresses)
            {
                HashSet<Address> missingAddressesSet = missingAddresses.ToHashSet();

                foreach (KeyValuePair<Address, (UInt256 Balance, ulong Nonce)> accountState in testCase.AccountStates
                    .Where(v => !missingAddressesSet.Contains(v.Key)))
                {
                    stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                    for (ulong i = 0; i < accountState.Value.Nonce; i++)
                    {
                        stateProvider.IncrementNonce(accountState.Key);
                    }
                }

                stateProvider.Commit(Homestead.Instance);
                stateProvider.CommitTree(0);
            }

            SetAccountStates(testCase.MissingAddresses);

            Transaction[] selectedTransactions = RunBlockProduction(
                new BuildUpTransactionProcessorAdapter(transactionProcessor),
                stateProvider,
                specProvider,
                blockToProduce,
                spec);
            Assert.That(
                selectedTransactions.Select(static transaction => transaction.Hash),
                Is.EquivalentTo(testCase.ExpectedSelectedTransactions.Select(static transaction => transaction.Hash)));
        }

        [Test]
        public void BlockProductionTransactionsExecutor_calculates_block_size_using_proper_tx_form()
        {
            Transaction transactionInMempoolForm = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(1, true, Osaka.Instance)
                .SignedAndResolved()
                .TestObject;

            int payloadLength = TxPool.TransactionExtensions.GetLength(transactionInMempoolForm, false);
            int mempoolLength = TxPool.TransactionExtensions.GetLength(transactionInMempoolForm, true);

            Block block = Build.A.Block
                .WithExcessBlobGas(0)
                .WithGasLimit(GasCostOf.Transaction)
                .WithTransactions([transactionInMempoolForm])
                .TestObject;

            BlockToProduce blockToProduce = new(block.Header, block.Transactions, block.Uncles);

            ITransactionProcessorAdapter transactionProcessor = Substitute.For<ITransactionProcessorAdapter>();

            IWorldState stateProvider = new WorldStateStab();
            using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);

            IReleaseSpec spec = Osaka.Instance;
            ISpecProvider specProvider = new TestSingleReleaseSpecProvider(spec);

            BlockProcessor.BlockProductionTransactionPicker txPicker = new(specProvider, mempoolLength / 1.KiB - 1);
            BlockProcessor.BlockProductionTransactionsExecutor txExecutor = new(transactionProcessor, stateProvider, txPicker, LimboLogs.Instance, NullBlockAccessListManager.Instance);

            txExecutor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, spec));
            txExecutor.ProcessTransactions(blockToProduce, ProcessingOptions.ProducingBlock, new());

            Assert.That(blockToProduce.TxByteLength, Is.EqualTo(payloadLength));
        }

        [Test]
        public void BlockProductionTransactionsExecutor_tx_picker_uses_state_changes_from_previous_transactions()
        {
            IWorldState stateProvider = TestWorldStateFactory.CreateForTest();

            using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
            stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);

            Transaction firstTx = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithNonce(0)
                .WithGasPrice(1)
                .WithGasLimit(GasCostOf.Transaction)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;

            Transaction secondTx = Build.A.Transaction
                .WithSenderAddress(TestItem.AddressA)
                .WithNonce(1)
                .WithGasPrice(1)
                .WithGasLimit(GasCostOf.Transaction)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;

            Block block = Build.A.Block
                .WithGasLimit(GasCostOf.Transaction * 2)
                .WithTransactions([firstTx, secondTx])
                .TestObject;

            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            IReleaseSpec spec = Homestead.Instance;
            transactionProcessor.When(t => t.BuildUp(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()))
                .Do(info =>
                {
                    Transaction tx = info.Arg<Transaction>();
                    stateProvider.IncrementNonce(tx.SenderAddress!);
                    stateProvider.SubtractFromBalance(tx.SenderAddress!, tx.Value + ((UInt256)tx.GasLimit * tx.GasPrice), spec);
                });

            Transaction[] selectedTransactions = RunBlockProduction(
                new BuildUpTransactionProcessorAdapter(transactionProcessor),
                stateProvider,
                block,
                spec);
            Assert.That(selectedTransactions, Has.Length.EqualTo(2));
            Assert.That(selectedTransactions[0], Is.SameAs(firstTx));
            Assert.That(selectedTransactions[1], Is.SameAs(secondTx));
        }

        // EIP-8141: the pool exempts frame transactions from EIP-3607, so without the same exemption
        // here a smart account's own transaction is admitted and gossiped but never built into a block.
        [TestCase(TxType.EIP1559, "Sender is contract", TestName = "CanAddTransaction_ContractSender_OrdinaryTx_Skipped")]
        [TestCase(TxType.FrameTx, null, TestName = "CanAddTransaction_ContractSender_FrameTx_Added")]
        public void CanAddTransaction_ContractSender_ExemptsOnlyFrameTransactions(TxType txType, string? expectedSkipReason)
        {
            IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
            using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
            IReleaseSpec spec = Prague.Instance;
            stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
            byte[] code = [0x00];
            stateProvider.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, spec);

            Transaction tx = new()
            {
                Type = txType,
                SenderAddress = TestItem.AddressA,
                Nonce = 0,
                GasLimit = GasCostOf.Transaction,
                GasPrice = 1,
                DecodedMaxFeePerGas = 1,
                Frames = txType == TxType.FrameTx ? [] : null,
                FrameSignatures = txType == TxType.FrameTx ? [] : null,
            };

            Block block = Build.A.Block.WithGasLimit(GasCostOf.Transaction * 2).TestObject;
            BlockProcessor.BlockProductionTransactionPicker picker = new(new TestSingleReleaseSpecProvider(spec));

            BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(block, tx, new HashSet<Transaction>(), stateProvider);

            Assert.That(args.Action, Is.EqualTo(expectedSkipReason is null ? BlockProcessor.TxAction.Add : BlockProcessor.TxAction.Skip));
            if (expectedSkipReason is not null) Assert.That(args.Reason, Is.EqualTo(expectedSkipReason));
        }

        // EIP-8141: a frame transaction's GasLimit is only the sum of its frame gas limits, so the picker must gate on
        // max_gas or the produced block exceeds its own gas limit. The mandatory cost of the single frame below is
        // FRAME_TX_INTRINSIC_COST + FRAME_TX_PER_FRAME_COST = 15,475, and each non-zero calldata byte adds 16 to the
        // standard cost against 40 to the EIP-7623 floor, so the last case fits the block on its standard cost alone.
        [TestCase(100_000UL, 0, false)]
        [TestCase(115_000UL, 0, true)]
        [TestCase(10_000UL, 4000, true)]
        public void Frame_transaction_is_gated_on_its_max_gas(ulong frameGasLimit, int frameDataLength, bool expectedSkipped)
        {
            IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
            using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
            stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);

            byte[] frameData = Enumerable.Repeat((byte)1, frameDataLength).ToArray();
            Transaction frameTx = new()
            {
                Type = TxType.FrameTx,
                Nonce = 0,
                SenderAddress = TestItem.AddressA,
                Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, frameGasLimit, UInt256.Zero, frameData)],
                FrameSignatures = [],
                GasLimit = frameGasLimit,
                GasPrice = 1,
                DecodedMaxFeePerGas = 1,
            };
            frameTx.Hash = frameTx.CalculateHash();

            Block block = Build.A.Block.WithGasLimit(130_000).WithTransactions([frameTx]).TestObject;
            ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Bogota.Instance);
            BlockProcessor.BlockProductionTransactionPicker picker = new(specProvider);

            BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(block, frameTx, new HashSet<Transaction>(), stateProvider);

            if (expectedSkipped)
            {
                Assert.That(args.Action, Is.EqualTo(BlockProcessor.TxAction.Skip));
                Assert.That(args.Reason, Does.StartWith("Not enough gas in block"));
            }
            else
            {
                Assert.That(args.Action, Is.EqualTo(BlockProcessor.TxAction.Add), args.Reason);
            }
        }

        [Test]
        public void CanAddTransaction_skips_blob_carrying_frame_transaction()
        {
            // EIP8141: blob-carrying frame txs are routed to the blob pool and metered against the block
            // blob budget by the blob-selection path, so they do not reach this normal-pool picker in the
            // standard flow. The picker still excludes any that arrive here (defense in depth): without a
            // resolvable EIP-7594 sidecar they cannot be produced with a complete blobs bundle.
            IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
            using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
            stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);

            ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Eip8141Prototype.Instance);

            Transaction frameBlobTx = Build.A.Transaction
                .WithType(TxType.FrameTx)
                .WithBlobVersionedHashes(1)
                .WithSenderAddress(TestItem.AddressA)
                .WithNonce(0)
                .WithGasLimit(GasCostOf.Transaction)
                .TestObject;

            Block block = Build.A.Block
                .WithNumber(1)
                .WithExcessBlobGas(0)
                .WithGasLimit(30_000_000)
                .TestObject;

            BlockProcessor.BlockProductionTransactionPicker picker = new(specProvider);
            BlockProcessor.AddingTxEventArgs args =
                picker.CanAddTransaction(block, frameBlobTx, new HashSet<Transaction>(), stateProvider);

            Assert.That(args.Action, Is.EqualTo(BlockProcessor.TxAction.Skip));
            Assert.That(args.Reason, Does.Contain("frame"));
        }

        private static Transaction[] RunBlockProduction(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            ISpecProvider specProvider,
            BlockToProduce blockToProduce,
            IReleaseSpec spec)
        {
            BlockProcessor.BlockProductionTransactionsExecutor txExecutor = new(
                transactionProcessor,
                stateProvider,
                new BlockProcessor.BlockProductionTransactionPicker(specProvider, BlocksConfig.DefaultMaxTxKilobytes),
                LimboLogs.Instance,
                NullBlockAccessListManager.Instance);

            BlockReceiptsTracer receiptsTracer = new();
            receiptsTracer.StartNewBlockTrace(blockToProduce);
            txExecutor.SetBlockExecutionContext(new BlockExecutionContext(blockToProduce.Header, spec));
            txExecutor.ProcessTransactions(blockToProduce, ProcessingOptions.ProducingBlock, receiptsTracer);

            return blockToProduce.Transactions.ToArray();
        }

        private static Transaction[] RunBlockProduction(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            Block block,
            IReleaseSpec spec)
        {
            BlockToProduce blockToProduce = new(block.Header, block.Transactions, block.Uncles);
            return RunBlockProduction(transactionProcessor, stateProvider, new TestSingleReleaseSpecProvider(spec), blockToProduce, spec);
        }
    }

    public class WorldStateStab()
        : WorldStateDecorator(Substitute.For<IWorldState>())
    {
        public static IReadOnlyStateProvider GetUntrackedReader() => TestReadOnlyStateProvider.Instance;

        public override bool TryGetAccount(Address address, out AccountStruct account)
        {
            account = new(0ul, ulong.MaxValue);
            return true;
        }

        private sealed class TestReadOnlyStateProvider : IReadOnlyStateProvider
        {
            public static TestReadOnlyStateProvider Instance { get; } = new();

            public Hash256 StateRoot => Keccak.EmptyTreeHash;

            public bool TryGetAccount(Address address, out AccountStruct account)
            {
                account = new(0ul, ulong.MaxValue);
                return true;
            }

            public byte[] GetCode(Address address) => [];

            public byte[] GetCode(in ValueHash256 codeHash) => [];

            public bool IsContract(Address address) => false;

            public bool AccountExists(Address address) => true;

            public bool IsDeadAccount(Address address) => false;

            public ReadOnlySpan<byte> Get(in StorageCell storageCell) => [];
        }
    }
}
