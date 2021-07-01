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
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class TxCertifierFilterTests
    {
        private ICertifierContract _certifierContract;
        private ITxFilter _notCertifiedFilter;
        private TxCertifierFilter _filter;
        private ISpecProvider _specProvider;

        [SetUp]
        public void SetUp()
        {
            _certifierContract = Substitute.For<ICertifierContract>();
            _notCertifiedFilter = Substitute.For<ITxFilter>();
            _specProvider = Substitute.For<ISpecProvider>();
            
            _notCertifiedFilter.IsAllowed(Arg.Any<Transaction>(), Arg.Any<BlockHeader>())
                .Returns((false, string.Empty));
            
            _certifierContract.Certified(Arg.Any<BlockHeader>(), 
                Arg.Is<Address>(a => TestItem.Addresses.Take(3).Contains(a)))
                .Returns(true);
            
            _filter = new TxCertifierFilter(_certifierContract, _notCertifiedFilter, _specProvider, LimboLogs.Instance);
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
        
        [Test]
        public void should_not_allow_addresses_on_contract_error()
        {
            Address address = TestItem.Addresses.First();
            _certifierContract.Certified(Arg.Any<BlockHeader>(), address).Throws(new AbiException(string.Empty));
            ShouldAllowAddress(address, expected: false);
        }
        
        [TestCase(false)]
        [TestCase(true)]
        public void should_default_to_inner_contract_on_non_zero_transactions(bool expected)
        {
            _notCertifiedFilter.IsAllowed(Arg.Any<Transaction>(), Arg.Any<BlockHeader>())
                .Returns((expected, string.Empty));
            
            ShouldAllowAddress(TestItem.Addresses.First(), 1ul, expected);
        }
        
        private void ShouldAllowAddress(Address address, ulong gasPrice = 0ul, bool expected = true)
        {
            _filter.IsAllowed(
                Build.A.Transaction.WithGasPrice(gasPrice).WithSenderAddress(address).TestObject,
                Build.A.BlockHeader.TestObject).Allowed.Should().Be(expected);
        }

        [Test]
        public async Task should_only_allow_addresses_from_contract_on_chain()
        {
            using var chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
            chain.CertifierContract.Certified(chain.BlockTree.Head.Header, TestItem.AddressA).Should().BeFalse();
            chain.CertifierContract.Certified(chain.BlockTree.Head.Header, new Address("0xbbcaa8d48289bb1ffcf9808d9aa4b1d215054c78")).Should().BeTrue();
        }
        
        [Test]
        public async Task registry_contract_returns_correct_address()
        {
            using var chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
            chain.RegisterContract.TryGetAddress(chain.BlockTree.Head.Header, CertifierContract.ServiceTransactionContractRegistryName, out Address address).Should().BeTrue();
            address.Should().Be(new Address("0x5000000000000000000000000000000000000001"));
        }
        
        [Test]
        public async Task registry_contract_returns_not_found_when_key_doesnt_exist()
        {
            using var chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
            chain.RegisterContract.TryGetAddress(chain.BlockTree.Head.Header, "not existing key", out Address _).Should().BeFalse();
        }
        
        [Test]
        public async Task registry_contract_returns_not_found_when_contract_doesnt_exist()
        {
            using var chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
            var contract = new RegisterContract(AbiEncoder.Instance, Address.FromNumber(1000), chain.ReadOnlyTransactionProcessorSource);
            contract.TryGetAddress(chain.BlockTree.Head.Header, CertifierContract.ServiceTransactionContractRegistryName, out Address _).Should().BeFalse();
        }
        
        public class TestTxPermissionsBlockchain : TestContractBlockchain
        {
            public ReadOnlyTxProcessingEnv ReadOnlyTransactionProcessorSource { get; private set; }
            public RegisterContract RegisterContract { get; private set; }
            public CertifierContract CertifierContract { get; private set; }
            
            protected override BlockProcessor CreateBlockProcessor()
            {
                AbiEncoder abiEncoder = AbiEncoder.Instance;
                ReadOnlyTransactionProcessorSource = new(
                    DbProvider,
                    new TrieStore(DbProvider.StateDb, LimboLogs.Instance).AsReadOnly(),
                BlockTree, SpecProvider,
                    LimboLogs.Instance);
                RegisterContract = new RegisterContract(abiEncoder, ChainSpec.Parameters.Registrar, ReadOnlyTransactionProcessorSource);
                CertifierContract = new CertifierContract(
                    abiEncoder, 
                    RegisterContract,
                    ReadOnlyTransactionProcessorSource);
                
                return new AuRaBlockProcessor(
                    SpecProvider,
                    Always.Valid,
                    new RewardCalculator(SpecProvider),
                    new BlockProcessor.BlockValidationTransactionsExecutor(TxProcessor, State),
                    State,
                    Storage,
                    ReceiptStorage,
                    LimboLogs.Instance,
                    BlockTree);
            }

            protected override Task AddBlocksOnStart() => Task.CompletedTask;
        }
    }
}
