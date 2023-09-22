// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool.Comparison;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
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
                    .WithMaxFeePerGas(100.GWei())
                    .WithGasPrice(100.GWei())
                    .WithNonce(1)
                    .WithChainId(TestBlockchainIds.ChainId)
                    .To(null)
                    .WithData(initCodeAboveTheLimit)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                Transaction txAboveTheLimitNoContract = Build.A.Transaction
                    .WithSignature(signature)
                    .WithGasLimit(10000000)
                    .WithMaxFeePerGas(100.GWei())
                    .WithGasPrice(100.GWei())
                    .WithNonce(1)
                    .WithChainId(TestBlockchainIds.ChainId)
                    .To(TestItem.AddressB)
                    .WithData(initCodeAboveTheLimit)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                Transaction txBelowTheLimit = Build.A.Transaction
                    .WithSignature(signature)
                    .WithGasLimit(10000000)
                    .WithMaxFeePerGas(100.GWei())
                    .WithGasPrice(100.GWei())
                    .WithNonce(2)
                    .WithChainId(TestBlockchainIds.ChainId)
                    .To(null)
                    .WithData(initCodeBelowTheLimit)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

                TransactionSelectorTests.ProperTransactionsSelectedTestCase shanghai3860Scenarios = new()
                {
                    ReleaseSpec = Shanghai.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (30000000.Ether(), 1) } },
                    Transactions = new List<Transaction>() { txAboveTheLimit, txAboveTheLimitNoContract, txBelowTheLimit },
                    GasLimit = 10000000
                };
                shanghai3860Scenarios.ExpectedSelectedTransactions.AddRange(
                    new[] { 1, 2 }.Select(i => shanghai3860Scenarios.Transactions[i]));
                yield return new TestCaseData(shanghai3860Scenarios).SetName("EIP3860 enabled scenarios");

                TransactionSelectorTests.ProperTransactionsSelectedTestCase london3860Scenarios = new()
                {
                    ReleaseSpec = London.Instance,
                    BaseFee = 5,
                    AccountStates = { { TestItem.AddressA, (30000000.Ether(), 1) } },
                    Transactions = new List<Transaction>() { txAboveTheLimit },
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
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb codeDb = new();
            TrieStore trieStore = new(stateDb, LimboLogs.Instance);
            WorldState stateProvider = new(trieStore, codeDb, LimboLogs.Instance);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();

            IReleaseSpec spec = testCase.ReleaseSpec;
            specProvider.GetSpec(Arg.Any<long>(), Arg.Any<ulong?>()).Returns(spec);
            specProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(spec);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            transactionProcessor.When(t => t.BuildUp(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>()))
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

                foreach (KeyValuePair<Address, (UInt256 Balance, UInt256 Nonce)> accountState in testCase.AccountStates
                    .Where(v => !missingAddressesSet.Contains(v.Key)))
                {
                    stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                    for (int i = 0; i < accountState.Value.Nonce; i++)
                    {
                        stateProvider.IncrementNonce(accountState.Key);
                    }
                }

                stateProvider.Commit(Homestead.Instance);
                stateProvider.CommitTree(0);
            }

            BlockProcessor.BlockProductionTransactionsExecutor txExecutor =
                new(
                    transactionProcessor,
                    stateProvider,
                    specProvider,
                    LimboLogs.Instance);

            SetAccountStates(testCase.MissingAddresses);

            BlockReceiptsTracer receiptsTracer = new();
            receiptsTracer.StartNewBlockTrace(blockToProduce);

            txExecutor.ProcessTransactions(blockToProduce, ProcessingOptions.ProducingBlock, receiptsTracer, spec);
            blockToProduce.Transactions.Should().BeEquivalentTo(testCase.ExpectedSelectedTransactions);
        }
    }
}
