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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class TxPriorityContractTests
    {
        private static readonly byte[] FnSignature = {0, 1, 2, 3};
        
        [Test]
        public async Task whitelist_empty_after_init()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var whiteList = chain.TxPriorityContract.SendersWhitelist.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            whiteList.Should().BeEmpty();
        }
        
        [Test]
        public async Task priorities_empty_after_init()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var priorities = chain.TxPriorityContract.Priorities.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            priorities.Should().BeEmpty();
        }
        
        [Test]
        public async Task mingas_empty_after_init()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var minGas = chain.TxPriorityContract.MinGasPrices.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            minGas.Should().BeEmpty();
        }
        
        [Test]
        public async Task whitelist_should_return_correctly()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocks, TxPriorityContractTests>();
            var whiteList = chain.TxPriorityContract.SendersWhitelist.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            var whiteListInContract = chain.SendersWhitelist.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            object[] expected = {TestItem.AddressA, TestItem.AddressC};
            whiteList.Should().BeEquivalentTo(expected);
            whiteListInContract.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public async Task priority_should_return_correctly()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocks, TxPriorityContractTests>();
            var priorities = chain.TxPriorityContract.Priorities.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            var prioritiesInContract = chain.Priorities.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            object[] expected =
            {
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature, 3),
                new TxPriorityContract.Destination(TestItem.AddressA, TxPriorityContract.Destination.FnSignatureEmpty, UInt256.One),
            };
            
            priorities.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>());
            prioritiesInContract.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>());
        }
        
        [Test]
        public async Task mingas_should_return_correctly()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocks, TxPriorityContractTests>();
            var minGasPrices = chain.TxPriorityContract.MinGasPrices.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            var minGasPricesInContract = chain.MinGasPrices.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            object[] expected = {new TxPriorityContract.Destination(TestItem.AddressB, FnSignature, 4)};
            minGasPrices.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>());
            minGasPricesInContract.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>());
        }

        public class TxPermissionContractBlockchain : TestContractBlockchain
        {
            public TxPriorityContract TxPriorityContract { get; private set; }
            public SortedListContractDataStore<TxPriorityContract.Destination> Priorities { get; private set; }
            
            public DictionaryContractDataStore<TxPriorityContract.Destination> MinGasPrices { get; private set; }
            
            public HashSetContractDataStore<Address> SendersWhitelist { get; private set; }
            
            protected override TxPoolTxSource CreateTxPoolTxSource()
            {
                TxPoolTxSource txPoolTxSource = base.CreateTxPoolTxSource();
                
                TxPriorityContract = new TxPriorityContract(new AbiEncoder(), TestItem.AddressA, 
                    new ReadOnlyTxProcessorSource(DbProvider, TrieStore, BlockTree, SpecProvider, LimboLogs.Instance));

                var comparer = TxPriorityContract.DestinationMethodComparer.Instance;
                Priorities = new SortedListContractDataStore<TxPriorityContract.Destination>(TxPriorityContract.Priorities, BlockProcessor, comparer);
                MinGasPrices = new DictionaryContractDataStore<TxPriorityContract.Destination>(TxPriorityContract.MinGasPrices, BlockProcessor, comparer);
                SendersWhitelist = new HashSetContractDataStore<Address>(TxPriorityContract.SendersWhitelist, BlockProcessor);
                return txPoolTxSource;
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }

        public class TxPermissionContractBlockchainWithBlocks : TxPermissionContractBlockchain
        {
            protected override Task AddBlocksOnStart()
            {
                EthereumEcdsa ecdsa = new EthereumEcdsa(ChainSpec.ChainId, LimboLogs.Instance);

                return AddBlock(
                    SignTransactions(ecdsa, TestItem.PrivateKeyA,
                        TxPriorityContract.SetPriority(TestItem.AddressA, TxPriorityContract.Destination.FnSignatureEmpty, UInt256.One),
                        TxPriorityContract.SetPriority(TestItem.AddressB, FnSignature, 3),

                        TxPriorityContract.SetMinGasPrice(TestItem.AddressB, FnSignature, 2),
                        TxPriorityContract.SetMinGasPrice(TestItem.AddressB, FnSignature, 4),

                        TxPriorityContract.SetSendersWhitelist(TestItem.AddressA, TestItem.AddressB),
                        TxPriorityContract.SetSendersWhitelist(TestItem.AddressA, TestItem.AddressC))
                );
            }

            private Transaction[] SignTransactions(IEthereumEcdsa ecdsa, PrivateKey key, params Transaction[] transactions)
            {
                for (var index = 0; index < transactions.Length; index++)
                {
                    Transaction transaction = transactions[index];
                    ecdsa.Sign(key, transaction, true);
                    transaction.SenderAddress = key.Address;
                    transaction.Nonce = (UInt256) (index + 1);
                    transaction.Hash = transaction.CalculateHash();
                }

                return transactions;
            }
        }
    }
}
