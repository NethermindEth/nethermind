// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Test.Contract;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions;

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

        _notCertifiedFilter.IsAllowed(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>())
            .Returns(AcceptTxResult.Invalid);

        _certifierContract.Certified(Arg.Any<BlockHeader>(),
            Arg.Is<Address>(static a => TestItem.Addresses.Take(3).Contains(a)))
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

    [Test]
    public void should_not_allow_null_sender()
    {
        ShouldAllowAddress(null, expected: false);
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
        _notCertifiedFilter.IsAllowed(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>())
            .Returns(expected ? AcceptTxResult.Accepted : AcceptTxResult.Invalid);

        ShouldAllowAddress(TestItem.Addresses.First(), 1ul, expected);
    }

    private void ShouldAllowAddress(Address? address, ulong gasPrice = 0ul, bool expected = true)
    {
        _filter.IsAllowed(
            Build.A.Transaction.WithGasPrice(gasPrice).WithSenderAddress(address).TestObject,
            Build.A.BlockHeader.TestObject, Substitute.For<IReleaseSpec>()).Equals(AcceptTxResult.Accepted).Should().Be(expected);
    }

    [Test]
    public async Task should_only_allow_addresses_from_contract_on_chain()
    {
        using TestTxPermissionsBlockchain chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
        chain.CertifierContract.Certified(chain.BlockTree.Head.Header, TestItem.AddressA).Should().BeFalse();
        chain.CertifierContract.Certified(chain.BlockTree.Head.Header, new Address("0xbbcaa8d48289bb1ffcf9808d9aa4b1d215054c78")).Should().BeTrue();
    }

    [Test]
    public async Task registry_contract_returns_correct_address()
    {
        using TestTxPermissionsBlockchain chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
        chain.RegisterContract.TryGetAddress(chain.BlockTree.Head.Header, CertifierContract.ServiceTransactionContractRegistryName, out Address address).Should().BeTrue();
        address.Should().Be(new Address("0x5000000000000000000000000000000000000001"));
    }

    [Test]
    public async Task registry_contract_returns_not_found_when_key_doesnt_exist()
    {
        using TestTxPermissionsBlockchain chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
        chain.RegisterContract.TryGetAddress(chain.BlockTree.Head.Header, "not existing key", out Address _).Should().BeFalse();
    }

    [Test]
    public async Task registry_contract_returns_not_found_when_contract_doesnt_exist()
    {
        using TestTxPermissionsBlockchain chain = await TestContractBlockchain.ForTest<TestTxPermissionsBlockchain, TxCertifierFilterTests>();
        RegisterContract contract = new(AbiEncoder.Instance, Address.FromNumber(1000), chain.ReadOnlyTxProcessingEnvFactory.Create());
        contract.TryGetAddress(chain.BlockTree.Head.Header, CertifierContract.ServiceTransactionContractRegistryName, out Address _).Should().BeFalse();
    }

    public class TestTxPermissionsBlockchain : TestContractBlockchain
    {
        public RegisterContract RegisterContract { get; private set; }
        public CertifierContract CertifierContract { get; private set; }

        protected override async Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
        {
            TestBlockchain blockchain = await base.Build(configurer);
            AbiEncoder abiEncoder = AbiEncoder.Instance;
            RegisterContract = new RegisterContract(abiEncoder, ChainSpec.Parameters.Registrar, ReadOnlyTxProcessingEnvFactory.Create());
            CertifierContract = new CertifierContract(
                abiEncoder,
                RegisterContract,
                ReadOnlyTxProcessingEnvFactory.Create());
            return blockchain;
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;
    }
}
