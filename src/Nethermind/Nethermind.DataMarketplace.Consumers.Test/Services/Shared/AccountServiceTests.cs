// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Consumers.DataStreams;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Notifiers.Services;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Consumers.Shared.Services;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Shared
{
    [TestFixture]
    public class AccountServiceTests
    {
        private AccountService _accountService;
        private DevWallet _wallet;
        private Address _consumerAddress;
        private IProviderService _providerService;
        private INdmNotifier _ndmNotifier;

        [SetUp]
        public void Setup()
        {
            IConfigManager configManager = Substitute.For<IConfigManager>();
            configManager.GetAsync(null).ReturnsForAnyArgs(new NdmConfig());
            IDataStreamService dataStreamService = Substitute.For<IDataStreamService>();
            _providerService = Substitute.For<IProviderService>();
            ISessionService sessionService = Substitute.For<ISessionService>();
            _ndmNotifier = Substitute.For<INdmNotifier>();
            IConsumerNotifier consumerNotifier = new ConsumerNotifier(_ndmNotifier);
            _wallet = new DevWallet(new WalletConfig(), LimboLogs.Instance);
            _consumerAddress = _wallet.GetAccounts()[0];
            _accountService = new AccountService(configManager, dataStreamService, _providerService, sessionService, consumerNotifier, _wallet, "configId", _consumerAddress, LimboLogs.Instance);
        }

        [Test]
        public void Consumer_address_is_as_expected()
        {
            Address address = _accountService.GetAddress();
            address.Should().Be(_consumerAddress);
        }

        [Test]
        public void When_no_change_no_notifications_happen()
        {
            Address newConsumerAddress = _consumerAddress;
            _accountService.ChangeAddressAsync(newConsumerAddress);
            _providerService.DidNotReceive().GetPeers();
        }

        [Test]
        public void On_change_providers_get_notifications()
        {
            Address newConsumerAddress = TestItem.AddressB;
            _accountService.ChangeAddressAsync(newConsumerAddress);
            _providerService.Received().GetPeers();
        }

        [Test]
        public void Notifies_on_account_locked()
        {
            _wallet.LockAccount(_consumerAddress);
            _ndmNotifier.ReceivedWithAnyArgs().NotifyAsync(null);
        }

        [Test]
        public void Notifies_on_account_unlocked()
        {
            _wallet.LockAccount(_consumerAddress);
            _wallet.UnlockAccount(_consumerAddress, new SecureString(), TimeSpan.FromMinutes(3));
            _ndmNotifier.ReceivedWithAnyArgs(2).NotifyAsync(null);
        }
    }
}
