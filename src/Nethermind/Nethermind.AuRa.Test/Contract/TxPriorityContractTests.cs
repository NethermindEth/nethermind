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
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    public class TxPriorityContractTests
    {
        private const string ContractAddress = "0xAB5b100cf7C8deFB3c8f3C48474223997A50fB13";
        
        [Test]
        public async Task whitelist_empty_after_init()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var whiteList = chain.TxPriorityContract.SendersWhitelist.GetAll(chain.BlockTree.Head.Header);
            whiteList.Should().BeEmpty();
        }
        
        [Test]
        public async Task priorities_empty_after_init()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var priorities = chain.TxPriorityContract.Priorities.GetAll(chain.BlockTree.Head.Header);
            priorities.Should().BeEmpty();
        }
        
        [Test]
        public async Task mingas_empty_after_init()
        {
            var chain = await TestContractBlockchain.ForTest<TxPermissionContractBlockchain, TxPriorityContractTests>();
            var minGas = chain.TxPriorityContract.MinGasPrice.GetAll(chain.BlockTree.Head.Header);
            minGas.Should().BeEmpty();
        }

        public class TxPermissionContractBlockchain : TestContractBlockchain
        {
            public TxPriorityContract TxPriorityContract { get; private set; }
            
            protected override TxPoolTxSource CreateTxPoolTxSource()
            {
                TxPoolTxSource txPoolTxSource = base.CreateTxPoolTxSource();
                
                TxPriorityContract = new TxPriorityContract(new AbiEncoder(), new Address(ContractAddress), 
                    new ReadOnlyTxProcessorSource(DbProvider, BlockTree, SpecProvider, LimboLogs.Instance));
                
                txPoolTxSource.OrderStrategy = new PermissionTxPoolOrderStrategy(
                    new ContractDataStore<Address>(TxPriorityContract.SendersWhitelist, BlockProcessor),
                    new ContractDataStore<TxPriorityContract.Destination>(TxPriorityContract.Priorities, BlockProcessor),
                    new ContractDataStore<TxPriorityContract.Destination>(TxPriorityContract.MinGasPrice, BlockProcessor));
                
                return txPoolTxSource;
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }
    }
}
