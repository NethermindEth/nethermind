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
    public class TransactionsExecutorTests: TransactionsExecutorTestsBase
    {
        [TestCaseSource(nameof(ProperTransactionsSelectedTestCases))]
        public void Proper_transactions_selected(TransactionSelectorTestsBase.ProperTransactionsSelectedTestCase testCase)
        {
            MemDb stateDb = new();
            MemDb codeDb = new();
            TrieStore trieStore = new(stateDb, LimboLogs.Instance);
            StateProvider stateProvider = new(trieStore, codeDb, LimboLogs.Instance);
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

            TransactionComparerProvider transactionComparerProvider = new(specProvider, blockTree);
            IComparer<Transaction> defaultComparer = transactionComparerProvider.GetDefaultComparer();
            IComparer<Transaction> comparer = CompareTxByNonce.Instance.ThenBy(defaultComparer);
            Transaction[] txArray = testCase.Transactions.Where(t => t?.SenderAddress != null).OrderBy(t => t, comparer).ToArray();

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
                    storageProvider, 
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
