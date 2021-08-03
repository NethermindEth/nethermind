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

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class TxPriorityContractTests
    {
        private static readonly byte[] FnSignature = {0, 1, 2, 3};
        private static readonly byte[] FnSignature2 = TxPriorityContract.Destination.FnSignatureEmpty;
        
        [Test]
        public async Task whitelist_empty_after_init()
        {
            using var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var whiteList = chain.TxPriorityContract.SendersWhitelist.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            whiteList.Should().BeEmpty();
        }
        
        [Test]
        public async Task priorities_empty_after_init()
        {
            using var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var priorities = chain.TxPriorityContract.Priorities.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            priorities.Should().BeEmpty();
        }
        
        [Test]
        public async Task mingas_empty_after_init()
        {
            using var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var minGas = chain.TxPriorityContract.MinGasPrices.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            minGas.Should().BeEmpty();
        }
        
        [Test]
        public async Task whitelist_should_return_correctly()
        {
            using var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocks, TxPriorityContractTests>();
            var whiteList = chain.TxPriorityContract.SendersWhitelist.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            var whiteListInContract = chain.SendersWhitelist.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            object[] expected = {TestItem.AddressA, TestItem.AddressC};
            whiteList.Should().BeEquivalentTo(expected);
            whiteListInContract.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public async Task 
            priority_should_return_correctly()
        {
            using var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocks, TxPriorityContractTests>();
            var priorities = chain.TxPriorityContract.Priorities.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            var prioritiesInContract = chain.Priorities.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            object[] expected =
            {
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature, 3, TxPriorityContract.DestinationSource.Contract, 2),
                new TxPriorityContract.Destination(TestItem.AddressA, FnSignature2, 1, TxPriorityContract.DestinationSource.Contract, 1),
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature2, 4, TxPriorityContract.DestinationSource.Contract, 1),
            };
            
            priorities.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>()
                .Excluding(su => su.SelectedMemberPath.EndsWith("BlockNumber")));
            prioritiesInContract.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>());
        }
        
        [Test]
        [Retry(3)]
        public async Task mingas_should_return_correctly()
        {
            using var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocks, TxPriorityContractTests>();
            var minGasPrices = chain.TxPriorityContract.MinGasPrices.GetAllItemsFromBlock(chain.BlockTree.Head.Header);
            var minGasPricesInContract = chain.MinGasPrices.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            object[] expected =
            {
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature2, 4, TxPriorityContract.DestinationSource.Contract, 1),
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature, 2, TxPriorityContract.DestinationSource.Contract, 2),
            };

            minGasPrices.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>()
                .Excluding(su => su.SelectedMemberPath.EndsWith("BlockNumber")));
            
            minGasPricesInContract.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>());
        }
        
        [Test]
        [Retry(3)]
        [Explicit]
        public async Task whitelist_should_return_correctly_with_local_storage([Values(true, false)] bool fileFirst)
        {
            using var chain = fileFirst 
                ? await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocksAndLocalDataBeforeStart, TxPriorityContractTests>()
                : await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocksAndLocalData, TxPriorityContractTests>();

            var semaphoreSlim = new SemaphoreSlim(chain.LocalDataSource.Data != null ? 1 : 0);
            chain.LocalDataSource.Changed += (sender, args) =>
            {
                var localData = chain.LocalDataSource.Data;
                if (localData != null)
                {
                    localData.Whitelist.Should().BeEquivalentTo(new object[] {TestItem.AddressD, TestItem.AddressB});
                    semaphoreSlim.Release();
                }
            };
            
            if (!await chain.FileSemaphore.WaitAsync(100))
            {
                Assert.Fail("File not written");
            }
            
            if (!await semaphoreSlim.WaitAsync(100))
            {
                if (chain.LocalDataSource.Data == null)
                {
                    Assert.Fail("Local file rule storage has not been loaded.");
                }
            }

            object[] expected = {TestItem.AddressD, TestItem.AddressB, TestItem.AddressA, TestItem.AddressC};
            
            var whiteList = chain.SendersWhitelist.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            whiteList.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        [Retry(3)]
        [Explicit]
        public async Task priority_should_return_correctly_with_local_storage([Values(true, false)] bool fileFirst)
        {
            using var chain = fileFirst 
                ? await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocksAndLocalDataBeforeStart, TxPriorityContractTests>()
                : await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocksAndLocalData, TxPriorityContractTests>();
            
            TxPriorityContract.Destination[] expected =
            {
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature, 5, TxPriorityContract.DestinationSource.Local),
                new TxPriorityContract.Destination(TestItem.AddressC, FnSignature, 1, TxPriorityContract.DestinationSource.Local),
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature2, 1, TxPriorityContract.DestinationSource.Local),
                new TxPriorityContract.Destination(TestItem.AddressA, TxPriorityContract.Destination.FnSignatureEmpty, UInt256.One, TxPriorityContract.DestinationSource.Contract, 1),
            };
            
            var semaphoreSlim = new SemaphoreSlim(chain.LocalDataSource.Data != null ? 1 : 0);
            chain.LocalDataSource.Changed += (sender, args) =>
            {
                var localData = chain.LocalDataSource.Data;
                if (localData != null)
                {
                    chain.LocalDataSource.Data.Priorities.Should().BeEquivalentTo(
                        expected.Where(e => e.Source == TxPriorityContract.DestinationSource.Local), 
                        o => o.ComparingByMembers<TxPriorityContract.Destination>());
                    semaphoreSlim.Release();
                }
            };

            if (!await chain.FileSemaphore.WaitAsync(100))
            {
                Assert.Fail("File not written");
            }
            
            if (!await semaphoreSlim.WaitAsync(100))
            {
                if (chain.LocalDataSource.Data == null)
                {
                    Assert.Fail("Local file rule storage has not been loaded.");
                }
            }
            
            var priorities = chain.Priorities.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            priorities.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>());
        }

        [Test]
        [Retry(3)]
        [Explicit]
        public async Task mingas_should_return_correctly_with_local_storage([Values(true, false)] bool fileFirst)
        {
            using var chain = fileFirst 
                ? await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocksAndLocalDataBeforeStart, TxPriorityContractTests>()
                : await TestContractBlockchain.ForTest<TxPermissionContractBlockchainWithBlocksAndLocalData, TxPriorityContractTests>();
            
            TxPriorityContract.Destination[] expected =
            {
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature, 5, TxPriorityContract.DestinationSource.Local),
                new TxPriorityContract.Destination(TestItem.AddressB, FnSignature2, 1, TxPriorityContract.DestinationSource.Local),
                new TxPriorityContract.Destination(TestItem.AddressC, FnSignature, 1, TxPriorityContract.DestinationSource.Local),
            };
            
            var semaphoreSlim = new SemaphoreSlim(chain.LocalDataSource.Data != null ? 1 : 0);
            chain.LocalDataSource.Changed += (sender, args) =>
            {
                var localData = chain.LocalDataSource.Data;
                if (localData != null)
                {
                    chain.LocalDataSource.Data.MinGasPrices.Should().BeEquivalentTo(
                        expected.Where(e => e.Source == TxPriorityContract.DestinationSource.Local), 
                        o => o.ComparingByMembers<TxPriorityContract.Destination>());
                    semaphoreSlim.Release();
                }
            };
            
            if (!await chain.FileSemaphore.WaitAsync(100))
            {
                Assert.Fail("File not written");
            }
            
            if (!await semaphoreSlim.WaitAsync(100))
            {
                if (chain.LocalDataSource.Data == null)
                {
                    Assert.Fail("Local file rule storage has not been loaded.");
                }
            }
            
            var minGasPrices = chain.MinGasPrices.GetItemsFromContractAtBlock(chain.BlockTree.Head.Header);
            minGasPrices.Should().BeEquivalentTo(expected, o => o.ComparingByMembers<TxPriorityContract.Destination>());
        }

        public class TxPermissionContractBlockchain : TestContractBlockchain
        {
            public TxPriorityContract TxPriorityContract { get; private set; }
            public DictionaryContractDataStore<TxPriorityContract.Destination> Priorities { get; private set; }
            
            public DictionaryContractDataStore<TxPriorityContract.Destination> MinGasPrices { get; private set; }
            
            public ContractDataStoreWithLocalData<Address> SendersWhitelist { get; private set; }
            
            protected override TxPoolTxSource CreateTxPoolTxSource()
            {
                TxPoolTxSource txPoolTxSource = base.CreateTxPoolTxSource();
                
                TxPriorityContract = new TxPriorityContract(AbiEncoder.Instance, TestItem.AddressA, 
                    new ReadOnlyTxProcessingEnv(DbProvider, TrieStore.AsReadOnly(), BlockTree, SpecProvider, LimboLogs.Instance));

                Priorities = new DictionaryContractDataStore<TxPriorityContract.Destination>(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),  
                    TxPriorityContract.Priorities, 
                    BlockTree,
                    ReceiptStorage,
                    LimboLogs.Instance,
                    GetPrioritiesLocalDataStore());
                
                MinGasPrices = new DictionaryContractDataStore<TxPriorityContract.Destination>(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                    TxPriorityContract.MinGasPrices,
                    BlockTree,
                    ReceiptStorage,
                    LimboLogs.Instance,
                    GetMinGasPricesLocalDataStore());
                
                SendersWhitelist = new ContractDataStoreWithLocalData<Address>(new HashSetContractDataStoreCollection<Address>(),
                    TxPriorityContract.SendersWhitelist, 
                    BlockTree,
                    ReceiptStorage,
                    LimboLogs.Instance,
                    GetWhitelistLocalDataStore());
                
                return txPoolTxSource;
            }
            
            protected virtual ILocalDataSource<IEnumerable<Address>> GetWhitelistLocalDataStore() => new EmptyLocalDataSource<IEnumerable<Address>>();

            protected virtual ILocalDataSource<IEnumerable<TxPriorityContract.Destination>> GetMinGasPricesLocalDataStore() => null;

            protected virtual ILocalDataSource<IEnumerable<TxPriorityContract.Destination>> GetPrioritiesLocalDataStore() => null;

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }

        public class TxPermissionContractBlockchainWithBlocks : TxPermissionContractBlockchain
        {
            protected override async Task AddBlocksOnStart()
            {
                EthereumEcdsa ecdsa = new EthereumEcdsa(ChainSpec.ChainId, LimboLogs.Instance);

                await AddBlock(
                    SignTransactions(ecdsa, TestItem.PrivateKeyA, 1,
                        TxPriorityContract.SetPriority(TestItem.AddressA, FnSignature2, UInt256.One),
                        TxPriorityContract.SetPriority(TestItem.AddressB, FnSignature, 10),
                        TxPriorityContract.SetPriority(TestItem.AddressB, FnSignature2, 4),

                        TxPriorityContract.SetMinGasPrice(TestItem.AddressB, FnSignature, 10),
                        TxPriorityContract.SetMinGasPrice(TestItem.AddressB, FnSignature2, 4),
                        TxPriorityContract.SetSendersWhitelist(TestItem.AddressA, TestItem.AddressB))
                );

                await AddBlock(
                    SignTransactions(ecdsa, TestItem.PrivateKeyA, State.GetNonce(TestItem.PrivateKeyA.Address),
                        // overrides for some of previous block values:
                        TxPriorityContract.SetPriority(TestItem.AddressB, FnSignature, 3),

                        TxPriorityContract.SetMinGasPrice(TestItem.AddressB, FnSignature, 2),
                        
                        TxPriorityContract.SetSendersWhitelist(TestItem.AddressA, TestItem.AddressC))
                );
            }

            private Transaction[] SignTransactions(IEthereumEcdsa ecdsa, PrivateKey key, UInt256 baseNonce, params Transaction[] transactions)
            {
                for (var index = 0; index < transactions.Length; index++)
                {
                    Transaction transaction = transactions[index];
                    ecdsa.Sign(key, transaction, true);
                    transaction.SenderAddress = key.Address;
                    transaction.Nonce = (UInt256)index + baseNonce;
                    transaction.Hash = transaction.CalculateHash();
                }

                return transactions;
            }
        }

        public class TxPermissionContractBlockchainWithBlocksAndLocalData : TxPermissionContractBlockchainWithBlocks
        {
            public TxPriorityContract.LocalDataSource LocalDataSource { get; private set; }

            public TempPath TempFile { get; set; }

            private SemaphoreSlim Semaphore { get; set; }
            public SemaphoreSlim FileSemaphore { get; set; }
            
            public int Interval => 10;

            protected override ILocalDataSource<IEnumerable<TxPriorityContract.Destination>> GetPrioritiesLocalDataStore() => 
                LocalDataSource.GetPrioritiesLocalDataSource();

            protected override ILocalDataSource<IEnumerable<Address>> GetWhitelistLocalDataStore() => 
                LocalDataSource.GetWhitelistLocalDataSource();

            protected override ILocalDataSource<IEnumerable<TxPriorityContract.Destination>> GetMinGasPricesLocalDataStore() => 
                LocalDataSource.GetMinGasPricesLocalDataSource();

            protected override Task<TestBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
            {
                TempFile = TempPath.GetTempFile();
                LocalDataSource = new TxPriorityContract.LocalDataSource(TempFile.Path, new EthereumJsonSerializer(), new FileSystem(), LimboLogs.Instance, Interval);

                FileSemaphore = new SemaphoreSlim(0);
                Semaphore = new SemaphoreSlim(0);
                LocalDataSource.Changed += (o, e) => Semaphore.Release();

                return base.Build(specProvider, initialValues);
            }

            public override void Dispose()
            {
                base.Dispose();
                LocalDataSource?.Dispose();
                TempFile?.Dispose();
                Semaphore.Dispose();
                FileSemaphore?.Dispose();
            }

            protected virtual bool FileFirst => false;

            protected override TxPoolTxSource CreateTxPoolTxSource()
            {
                LocalData = new TxPriorityContract.LocalData()
                {
                    Priorities = new[]
                    {
                        new TxPriorityContract.Destination(TestItem.AddressB, FnSignature, 5),
                        new TxPriorityContract.Destination(TestItem.AddressC, FnSignature, 1),
                        new TxPriorityContract.Destination(TestItem.AddressB, FnSignature2, 1),
                    },
                    MinGasPrices = new[]
                    {
                        new TxPriorityContract.Destination(TestItem.AddressB, FnSignature, 5), 
                        new TxPriorityContract.Destination(TestItem.AddressC, FnSignature, 1),
                        new TxPriorityContract.Destination(TestItem.AddressB, FnSignature2, 1),
                    },
                    Whitelist = new[] {TestItem.AddressD, TestItem.AddressB}
                };
                
                return base.CreateTxPoolTxSource();
            }

            private TxPriorityContract.LocalData LocalData { get; set; }

            protected override async Task AddBlocksOnStart()
            {
                if (FileFirst)
                {
                    await AddFile();
                }
                
                await base.AddBlocksOnStart();

                if (!FileFirst)
                {
                    await AddFile();
                }

                await Semaphore.WaitAsync(100);
            }

            private async Task AddFile()
            {
                var fileSemaphore = new SemaphoreSlim(0);
                EventHandler releaseHandler = (sender, args) => fileSemaphore.Release();
                SendersWhitelist.Loaded += releaseHandler;
                ((ContractDataStoreWithLocalData<TxPriorityContract.Destination>) MinGasPrices.ContractDataStore).Loaded += releaseHandler;
                ((ContractDataStoreWithLocalData<TxPriorityContract.Destination>) Priorities.ContractDataStore).Loaded += releaseHandler;

                WriteFile(LocalData);
                FileSemaphore.Release();
                await fileSemaphore.WaitAsync(100);
                await fileSemaphore.WaitAsync(100);
                await fileSemaphore.WaitAsync(100);
            }

            private void WriteFile(TxPriorityContract.LocalData localData)
            {
                File.WriteAllText(TempFile.Path, new EthereumJsonSerializer().Serialize(localData));
            }
        }

        private class TxPermissionContractBlockchainWithBlocksAndLocalDataBeforeStart : TxPermissionContractBlockchainWithBlocksAndLocalData
        {
            protected override bool FileFirst => true;
        }
    }
}
