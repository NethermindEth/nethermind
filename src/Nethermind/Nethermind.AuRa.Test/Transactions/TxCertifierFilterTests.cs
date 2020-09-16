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

using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Test.Contract;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class TxCertifierFilterTests
    {
        private ICertifierContract _certifierContract;
        private ITxFilter _notCertifiedFilter;
        private TxCertifierFilter _filter;

        [SetUp]
        public void SetUp()
        {
            _certifierContract = Substitute.For<ICertifierContract>();
            _notCertifiedFilter = Substitute.For<ITxFilter>();
            
            _notCertifiedFilter.IsAllowed(Arg.Any<Transaction>(), Arg.Any<BlockHeader>())
                .Returns(false);
            
            _certifierContract.Certified(Arg.Any<BlockHeader>(), 
                Arg.Is<Address>(a => TestItem.Addresses.Take(3).Contains(a)))
                .Returns(true);
            
            _filter = new TxCertifierFilter(_certifierContract, _notCertifiedFilter, LimboLogs.Instance);
        }
        
        [Test]
        public void should_allow_addresses_from_contract()
        {
            ShouldAllowAddress(TestItem.Addresses.First());
            ShouldAllowAddress(TestItem.Addresses.First());
            ShouldAllowAddress(TestItem.Addresses.Skip(1).First());
            ShouldAllowAddress(TestItem.Addresses.Skip(2).First());
        }
        
        [Test]
        public void should_not_allow_addresses_from_outside_contract()
        {
            ShouldAllowAddress(TestItem.AddressA, expected: false);
        }
        
        [TestCase(false)]
        [TestCase(true)]
        public void should_default_to_inner_contract_on_non_zero_transactions(bool expected)
        {
            _notCertifiedFilter.IsAllowed(Arg.Any<Transaction>(), Arg.Any<BlockHeader>())
                .Returns(expected);
            
            ShouldAllowAddress(TestItem.Addresses.First(), 1ul, expected);
        }
        
        private void ShouldAllowAddress(Address address, ulong gasPrice = 0ul, bool expected = true)
        {
            _filter.IsAllowed(
                Build.A.Transaction.WithGasPrice(gasPrice).WithSenderAddress(address).TestObject,
                Build.A.BlockHeader.TestObject).Should().Be(expected);
        }

        [Test]
        public async Task should_only_allow_addresses_from_contract_on_chain()
        {
            var chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
            chain.CertifierContract.Certified(chain.BlockTree.Head.Header, TestItem.AddressA).Should().BeFalse();
            chain.CertifierContract.Certified(chain.BlockTree.Head.Header, new Address("0xbbcaa8d48289bb1ffcf9808d9aa4b1d215054c78")).Should().BeTrue();
        }
        
        public class TestTxPermissionsBlockchain : TestContractBlockchain
        {
            public CertifierContract CertifierContract { get; private set; }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                AbiEncoder abiEncoder = new AbiEncoder();
                ReadOnlyTxProcessorSource readOnlyTransactionProcessorSource = new ReadOnlyTxProcessorSource(
                    DbProvider, new TrieStore(DbProvider.StateDb, LimboLogs.Instance), BlockTree, SpecProvider, LimboLogs.Instance);
                CertifierContract = new CertifierContract(
                    abiEncoder, 
                    new RegisterContract(abiEncoder, ChainSpec.Parameters.Registrar, readOnlyTransactionProcessorSource),
                    readOnlyTransactionProcessorSource);
                
                return new AuRaBlockProcessor(
                    SpecProvider,
                    Always.Valid,
                    new RewardCalculator(SpecProvider),
                    TxProcessor,
                    StateDb,
                    CodeDb,
                    State,
                    Storage,
                    TxPool,
                    ReceiptStorage,
                    LimboLogs.Instance,
                    BlockTree);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }
    }
}
