// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.DataStreams;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Events;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.Shared.Services
{
    public class AccountService : IAccountService
    {
        private readonly IConfigManager _configManager;
        private readonly IDataStreamService _dataStreamService;
        private readonly IProviderService _providerService;
        private readonly ISessionService _sessionService;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly IWallet _wallet;
        private readonly string _configId;
        private Address _consumerAddress;
        private readonly ILogger _logger;

        public AccountService(
            IConfigManager configManager,
            IDataStreamService dataStreamService,
            IProviderService providerService,
            ISessionService sessionService,
            IConsumerNotifier consumerNotifier,
            IWallet wallet,
            string configId,
            Address consumerAddress,
            ILogManager logManager)
        {
            _configManager = configManager;
            _dataStreamService = dataStreamService;
            _providerService = providerService;
            _sessionService = sessionService;
            _consumerNotifier = consumerNotifier;
            _wallet = wallet;
            _configId = configId;
            _consumerAddress = consumerAddress;
            _logger = logManager.GetClassLogger();
            _wallet.AccountLocked += OnAccountLocked;
            _wallet.AccountUnlocked += OnAccountUnlocked;
        }

        public event EventHandler<AddressChangedEventArgs>? AddressChanged;

        public Address GetAddress() => _consumerAddress;
        public async Task ChangeAddressAsync(Address address)
        {
            if (_consumerAddress == address)
            {
                return;
            }

            Address previousAddress = _consumerAddress;
            if (_logger.IsInfo) _logger.Info($"Changing consumer address: '{previousAddress}' -> '{address}'...");
            _consumerAddress = address;
            AddressChanged?.Invoke(this, new AddressChangedEventArgs(previousAddress, _consumerAddress));
            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to change consumer address: '{previousAddress}' -> '{address}'...");
                return;
            }

            config.ConsumerAddress = _consumerAddress.ToString();
            await _configManager.UpdateAsync(config);

            foreach (INdmPeer provider in _providerService.GetPeers())
            {
                provider.ChangeHostConsumerAddress(_consumerAddress);
                provider.SendConsumerAddressChanged(_consumerAddress);
                await _sessionService.FinishSessionsAsync(provider, false);
            }

            await _consumerNotifier.SendConsumerAddressChangedAsync(address, previousAddress);
            if (_logger.IsInfo) _logger.Info($"Changed consumer address: '{previousAddress}' -> '{address}'.");
        }

        private void OnAccountUnlocked(object? sender, AccountUnlockedEventArgs e)
        {
            if (e.Address != _consumerAddress)
            {
                return;
            }

            _consumerNotifier.SendConsumerAccountLockedAsync(e.Address);

            if (_logger.IsInfo) _logger.Info($"Unlocked a consumer account: '{e.Address}', data streams can be enabled.");
        }

        private void OnAccountLocked(object? sender, AccountLockedEventArgs e)
        {
            if (e.Address != _consumerAddress)
            {
                return;
            }

            _consumerNotifier.SendConsumerAccountLockedAsync(e.Address);
            if (_logger.IsInfo) _logger.Info($"Locked a consumer account: '{e.Address}', all of the existing data streams will be disabled.");

            var sessions = _sessionService.GetAllActive();
            var disableStreamTasks = from session in sessions
                                     from client in session.Clients
                                     select _dataStreamService.DisableDataStreamAsync(session.DepositId, client.Id);

            Task.WhenAll(disableStreamTasks).ContinueWith(t =>
            {
                if (t.IsFaulted && _logger.IsError)
                {
                    _logger.Error("Disabling the data stream has failed.", t.Exception);
                }
            });
        }
    }
}
