//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Castle.Components.DictionaryAdapter;
using FluentAssertions;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
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
                ProperTransactionsSelectedTestCase noneTransactionSelectedDueToValue =
                    ProperTransactionsSelectedTestCase.Default;
                noneTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 901);
                yield return new TestCaseData(noneTransactionSelectedDueToValue).SetName(
                    "None transactions selected due to value");

                ProperTransactionsSelectedTestCase noneTransactionsSelectedDueToGasPrice =
                    ProperTransactionsSelectedTestCase.Default;
                noneTransactionsSelectedDueToGasPrice.Transactions.ForEach(t => t.GasPrice = 100);
                yield return new TestCaseData(noneTransactionsSelectedDueToGasPrice).SetName(
                    "None transactions selected due to transaction gas price and limit");

                ProperTransactionsSelectedTestCase oneTransactionSelectedDueToValue =
                    ProperTransactionsSelectedTestCase.Default;
                oneTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 500);
                oneTransactionSelectedDueToValue.ExpectedSelectedTransactions.AddRange(oneTransactionSelectedDueToValue
                    .Transactions.OrderBy(t => t.Nonce).Take(1));
                yield return new TestCaseData(oneTransactionSelectedDueToValue).SetName(
                    "One transaction selected due to gas limit and value");

                ProperTransactionsSelectedTestCase twoTransactionSelectedDueToValue =
                    ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 400);
                twoTransactionSelectedDueToValue.ExpectedSelectedTransactions.AddRange(twoTransactionSelectedDueToValue
                    .Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToValue).SetName(
                    "Two transaction selected due to gas limit and value");

                ProperTransactionsSelectedTestCase twoTransactionSelectedDueToMinGasPriceForMining =
                    ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToMinGasPriceForMining.MinGasPriceForMining = 2;
                twoTransactionSelectedDueToMinGasPriceForMining.ExpectedSelectedTransactions.AddRange(
                    twoTransactionSelectedDueToValue.Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToValue).SetName(
                    "Two transaction selected due to min gas price for mining");

                ProperTransactionsSelectedTestCase twoTransactionSelectedDueToWrongNonce =
                    ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToWrongNonce.Transactions.First().Nonce = 4;
                twoTransactionSelectedDueToWrongNonce.ExpectedSelectedTransactions.AddRange(
                    twoTransactionSelectedDueToWrongNonce.Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToWrongNonce).SetName(
                    "Two transaction selected due to wrong nonce");
                
                ProperTransactionsSelectedTestCase missingAddressState = ProperTransactionsSelectedTestCase.Default;
                missingAddressState.MissingAddresses.Add(TestItem.AddressA);
                yield return new TestCaseData(missingAddressState).SetName("Missing address state");

                ProperTransactionsSelectedTestCase complexCase = new ProperTransactionsSelectedTestCase()
                    {
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
                    new[] {7, 3, 4, 0, 2, 1}.Select(i => complexCase.Transactions[i]));
                yield return new TestCaseData(complexCase).SetName("Complex case");
                
                ProperTransactionsSelectedTestCase baseFeeBalanceCheck = new ProperTransactionsSelectedTestCase()
                {
                    Eip1559Enabled = true,
                    BaseFee = 5,
                    AccountStates = {{TestItem.AddressA, (1000, 1)}},
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
                    new[] {1, 2 }.Select(i => baseFeeBalanceCheck.Transactions[i]));
                yield return new TestCaseData(baseFeeBalanceCheck).SetName("Legacy transactions: two transactions selected because of account balance");
                
                ProperTransactionsSelectedTestCase balanceBelowMaxFeeTimesGasLimit = new ProperTransactionsSelectedTestCase()
                {
                    Eip1559Enabled = true,
                    BaseFee = 5,
                    AccountStates = {{TestItem.AddressA, (400, 1)}},
                    Transactions =
                    {
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1)
                            .WithMaxFeePerGas(45).WithMaxPriorityFeePerGas(25).WithGasLimit(10).WithType(TxType.EIP1559).WithValue(60).SignedAndResolved(TestItem.PrivateKeyA).TestObject
                    },
                    GasLimit = 10000000
                };
                yield return new TestCaseData(balanceBelowMaxFeeTimesGasLimit).SetName("EIP1559 transactions: none transactions selected because balance is lower than MaxFeePerGas times GasLimit");

                ProperTransactionsSelectedTestCase balanceFailingWithMaxFeePerGasCheck =
                    new ProperTransactionsSelectedTestCase()
                    {
                        Eip1559Enabled = true,
                        BaseFee = 5,
                        AccountStates = {{TestItem.AddressA, (400, 1)}},
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

        [TestCaseSource(nameof(ProperTransactionsSelectedTestCases))]
        public void Proper_transactions_selected(ProperTransactionsSelectedTestCase testCase)
        {
            MemDb stateDb = new MemDb();
            MemDb codeDb = new MemDb();
            TrieStore trieStore = new TrieStore(stateDb, LimboLogs.Instance);
            StateProvider stateProvider = new StateProvider(trieStore, codeDb, LimboLogs.Instance);
            IStorageProvider storageProvider = Substitute.For<IStorageProvider>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            
            IReleaseSpec spec = new ReleaseSpec()
            {
                IsEip1559Enabled = testCase.Eip1559Enabled
            };
            specProvider.GetSpec(Arg.Any<long>()).Returns(spec);
            
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            transactionProcessor.When(t => t.BuildUp(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<ITxTracer>()))
                .Do(info =>
                {
                    Transaction tx = info.Arg<Transaction>();
                    stateProvider.IncrementNonce(tx.SenderAddress!);
                    stateProvider.SubtractFromBalance(tx.SenderAddress!,
                        tx.Value + ((UInt256)tx.GasLimit * tx.GasPrice), spec);
                });
            
            IBlockTree blockTree = Substitute.For<IBlockTree>();

            TransactionComparerProvider transactionComparerProvider = new TransactionComparerProvider(specProvider, blockTree);
            IComparer<Transaction> defaultComparer = transactionComparerProvider.GetDefaultComparer();
            IComparer<Transaction> comparer = CompareTxByNonce.Instance.ThenBy(defaultComparer);
            Transaction[] txArray = testCase.Transactions.Where(t => t?.SenderAddress != null).OrderBy(t => t, comparer).ToArray();

            Block block = Build.A.Block
                .WithNumber(0)
                .WithBaseFeePerGas(testCase.BaseFee)
                .WithGasLimit(testCase.GasLimit)
                .WithTransactions(txArray)
                .TestObject;
            BlockToProduce blockToProduce = new BlockToProduce(block.Header, block.Transactions, block.Ommers);
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
                new BlockProcessor.BlockProductionTransactionsExecutor(
                    transactionProcessor, 
                    stateProvider, 
                    storageProvider, 
                    specProvider, 
                    LimboLogs.Instance);
            
            SetAccountStates(testCase.MissingAddresses);

            BlockReceiptsTracer receiptsTracer = new BlockReceiptsTracer();
            receiptsTracer.StartNewBlockTrace(blockToProduce);

            txExecutor.ProcessTransactions(blockToProduce, ProcessingOptions.ProducingBlock, receiptsTracer, spec);
            blockToProduce.Transactions.Should().BeEquivalentTo(testCase.ExpectedSelectedTransactions);
        }
    }
}
