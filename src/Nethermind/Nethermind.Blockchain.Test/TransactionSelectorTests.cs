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

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Castle.Components.DictionaryAdapter;
using FluentAssertions;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace Nethermind.Blockchain.Test
{
    public class TransactionSelectorTests
    {
        public static IEnumerable ProperTransactionsSelectedTestCases
        {
            get
            {
                var allTransactionsSelected = ProperTransactionsSelectedTestCase.Default;
                allTransactionsSelected.ExpectedSelectedTransactions.AddRange(allTransactionsSelected.Transactions);
                yield return new TestCaseData(allTransactionsSelected).SetName("All transactions selected");
                
                var noneTransactionSelectedDueToValue = ProperTransactionsSelectedTestCase.Default;
                noneTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 901);
                yield return new TestCaseData(noneTransactionSelectedDueToValue).SetName("None transactions selected due to value");
                
                var noneTransactionsSelectedDueToGasPrice = ProperTransactionsSelectedTestCase.Default;
                noneTransactionsSelectedDueToGasPrice.Transactions.ForEach(t => t.GasPrice = 100);
                yield return new TestCaseData(noneTransactionsSelectedDueToGasPrice).SetName("None transactions selected due to transaction gas price and limit");
                
                var noneTransactionsSelectedDueToGasLimit = ProperTransactionsSelectedTestCase.Default;
                noneTransactionsSelectedDueToGasLimit.GasLimit = 9;
                yield return new TestCaseData(noneTransactionsSelectedDueToGasLimit).SetName("None transactions selected due to gas limit");
                
                var oneTransactionSelectedDueToValue = ProperTransactionsSelectedTestCase.Default;
                oneTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 500);
                oneTransactionSelectedDueToValue.ExpectedSelectedTransactions.AddRange(oneTransactionSelectedDueToValue.Transactions.OrderBy(t => t.Nonce).Take(1));
                yield return new TestCaseData(oneTransactionSelectedDueToValue).SetName("One transaction selected due to gas limit and value");
                
                var twoTransactionSelectedDueToValue = ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToValue.Transactions.ForEach(t => t.Value = 400);
                twoTransactionSelectedDueToValue.ExpectedSelectedTransactions.AddRange(twoTransactionSelectedDueToValue.Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToValue).SetName("Two transaction selected due to gas limit and value");
                
                var twoTransactionSelectedDueToMinGasPriceForMining = ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToMinGasPriceForMining.MinGasPriceForMining = 2;
                twoTransactionSelectedDueToMinGasPriceForMining.ExpectedSelectedTransactions.AddRange(twoTransactionSelectedDueToValue.Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToValue).SetName("Two transaction selected due to min gas price for mining");

                var twoTransactionSelectedDueToWrongNonce = ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToWrongNonce.Transactions.First().Nonce = 4;
                twoTransactionSelectedDueToWrongNonce.ExpectedSelectedTransactions.AddRange(twoTransactionSelectedDueToWrongNonce.Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToWrongNonce).SetName("Two transaction selected due to wrong nonce");
                
                var twoTransactionSelectedDueToLackOfSenderAddress = ProperTransactionsSelectedTestCase.Default;
                twoTransactionSelectedDueToLackOfSenderAddress.Transactions.First().SenderAddress = null;
                twoTransactionSelectedDueToLackOfSenderAddress.ExpectedSelectedTransactions.AddRange(twoTransactionSelectedDueToLackOfSenderAddress.Transactions.OrderBy(t => t.Nonce).Take(2));
                yield return new TestCaseData(twoTransactionSelectedDueToLackOfSenderAddress).SetName("Two transaction selected due to lack of sender address");
                
                var complexCase = new ProperTransactionsSelectedTestCase()
                {
                    AccountStates = { {TestItem.AddressA, (1000, 1)}, {TestItem.AddressB, (1000, 0)}, {TestItem.AddressC, (1000, 3)} },
                    Transactions =
                    {
                        // A
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1).WithValue(10).WithGasPrice(10).WithGasLimit(10).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(3).WithValue(1).WithGasPrice(10).WithGasLimit(10).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).WithValue(10).WithGasPrice(10).WithGasLimit(10).TestObject,
                        
                        //B
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(0).WithValue(1).WithGasPrice(10).WithGasLimit(10).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(1).WithValue(1).WithGasPrice(10).WithGasLimit(9).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressB).WithNonce(3).WithValue(1).WithGasPrice(10).WithGasLimit(9).TestObject,
                        
                        //C
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressC).WithNonce(3).WithValue(500).WithGasPrice(19).WithGasLimit(9).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressC).WithNonce(3).WithValue(500).WithGasPrice(20).WithGasLimit(9).TestObject,
                        Build.A.Transaction.WithSenderAddress(TestItem.AddressC).WithNonce(4).WithValue(500).WithGasPrice(20).WithGasLimit(9).TestObject,
                    },
                    GasLimit = 10000
                };
                complexCase.ExpectedSelectedTransactions.AddRange(new[] {3, 4, 0, 2, 7, 1 }.Select(i => complexCase.Transactions[i]));
                yield return new TestCaseData(complexCase).SetName("Complex case");
            }
        }

        [TestCaseSource(nameof(ProperTransactionsSelectedTestCases))]
        public void Proper_transactions_selected(ProperTransactionsSelectedTestCase testCase)
        {
            var stateProvider = new StateProvider(new StateDb(new MemDb()), new MemDb(), NullLogManager.Instance);

            void SetAccountStates()
            {
                foreach (var accountState in testCase.AccountStates)
                {
                    stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance);
                    for (int i = 0; i < accountState.Value.Nonce; i++)
                    {
                        stateProvider.IncrementNonce(accountState.Key);
                    }
                }

                stateProvider.Commit(Homestead.Instance);
            }

            var transactionPool = Substitute.For<ITxPool>();
            transactionPool.GetPendingTransactions().Returns(testCase.Transactions.ToArray());
            SetAccountStates();

            var selector = new PendingTransactionSelector(transactionPool, stateProvider, NullLogManager.Instance, testCase.MinGasPriceForMining);

            var selectedTransactions = selector.SelectTransactions(testCase.GasLimit);
            selectedTransactions.Should().BeEquivalentTo(testCase.ExpectedSelectedTransactions);
        }
    }

    public class ProperTransactionsSelectedTestCase
    {
        public IDictionary<Address, (UInt256 Balance, UInt256 Nonce)> AccountStates { get; } = new Dictionary<Address, (UInt256 Balance, UInt256 Nonce)>();
        public List<Transaction> Transactions { get; } = new List<Transaction>();
        public long GasLimit { get; set; }
        public List<Transaction> ExpectedSelectedTransactions { get; } = new List<Transaction>();

        public long MinGasPriceForMining { get; set; } = 1;

        public static ProperTransactionsSelectedTestCase Default =>
            new ProperTransactionsSelectedTestCase()
            {
                AccountStates = { {TestItem.AddressA, (1000, 1)} },
                Transactions =
                {
                    Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(3).WithValue(1).WithGasPrice(10).WithGasLimit(10).TestObject,
                    Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(1).WithValue(10).WithGasPrice(10).WithGasLimit(10).TestObject,
                    Build.A.Transaction.WithSenderAddress(TestItem.AddressA).WithNonce(2).WithValue(10).WithGasPrice(10).WithGasLimit(10).TestObject
                },
                GasLimit = 1000
            };
    }
}
