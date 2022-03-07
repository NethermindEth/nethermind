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

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class VerkleTransactionSelectorTests: TransactionSelectorTestsBase
    {

        [TestCaseSource(nameof(ProperTransactionsSelectedTestCases))]
        [TestCaseSource(nameof(Eip1559LegacyTransactionTestCases))]
        [TestCaseSource(nameof(Eip1559TestCases))]
        public new void Proper_transactions_selected(ProperTransactionsSelectedTestCase testCase)
        {
            MemDb codeDb = new();
            VerkleTrieStore trieStore = new (DatabaseScheme.MemoryDb, CommitScheme.TestCommitment, LimboLogs.Instance);
            VerkleStateProvider stateProvider = new(trieStore, LimboLogs.Instance, codeDb);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();

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

            ITxPool transactionPool = Substitute.For<ITxPool>();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block block = Build.A.Block.WithNumber(0).TestObject;
            blockTree.Head.Returns(block);
            IReleaseSpec spec = new ReleaseSpec() {IsEip1559Enabled = testCase.Eip1559Enabled};
            specProvider.GetSpec(Arg.Any<long>()).Returns(spec);
            TransactionComparerProvider transactionComparerProvider =
                new(specProvider, blockTree);
            IComparer<Transaction> defaultComparer = transactionComparerProvider.GetDefaultComparer();
            IComparer<Transaction> comparer = CompareTxByNonce.Instance.ThenBy(defaultComparer);
            Dictionary<Address, Transaction[]> transactions = testCase.Transactions
                .Where(t => t?.SenderAddress != null)
                .GroupBy(t => t.SenderAddress)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(t => t, comparer).ToArray());
            transactionPool.GetPendingTransactionsBySender().Returns(transactions);
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(LimboLogs.Instance)
                .WithMinGasPriceFilter(testCase.MinGasPriceForMining, specProvider)
                .WithBaseFeeFilter(specProvider)
                .Build;

            SetAccountStates(testCase.MissingAddresses);

            TxPoolTxSource poolTxSource = new(transactionPool, specProvider,
                transactionComparerProvider, LimboLogs.Instance, txFilterPipeline);


            IEnumerable<Transaction> selectedTransactions =
                poolTxSource.GetTransactions(
                    Build.A.BlockHeader.WithStateRoot(stateProvider.StateRoot).WithBaseFee(testCase.BaseFee).TestObject,
                    testCase.GasLimit);
            selectedTransactions.Should()
                .BeEquivalentTo(testCase.ExpectedSelectedTransactions, o => o.WithStrictOrdering());
        }
    }
}
