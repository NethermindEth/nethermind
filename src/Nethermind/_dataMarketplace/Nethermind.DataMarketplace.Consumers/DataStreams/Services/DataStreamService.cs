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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.DataStreams.Services
{
    public class DataStreamService : IDataStreamService
    {
        private readonly IDataAssetService _dataAssetService;
        private readonly IDepositProvider _depositProvider;
        private readonly IProviderService _providerService;
        private readonly ISessionService _sessionService;
        private readonly IWallet _wallet;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly ILogger _logger;

        public DataStreamService(IDataAssetService dataAssetService, IDepositProvider depositProvider,
            IProviderService providerService, ISessionService sessionService, IWallet wallet,
            IConsumerNotifier consumerNotifier, IConsumerSessionRepository sessionRepository,  ILogManager logManager)
        {
            _dataAssetService = dataAssetService;
            _depositProvider = depositProvider;
            _providerService = providerService;
            _sessionService = sessionService;
            _wallet = wallet;
            _consumerNotifier = consumerNotifier;
            _sessionRepository = sessionRepository;
            _logger = logManager.GetClassLogger();
        }
        
        public Task<Keccak?> EnableDataStreamAsync(Keccak depositId, string client, string?[] args)
            => ToggleDataStreamAsync(depositId, true, client, args);

        public Task<Keccak?> DisableDataStreamAsync(Keccak depositId, string client)
            => ToggleDataStreamAsync(depositId, false, client);

        public async Task<Keccak?> DisableDataStreamsAsync(Keccak depositId)
        {
            ConsumerSession? session = _sessionService.GetActive(depositId);
            if (session is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Session for deposit: '{depositId}' was not found.");
                return null;
            }

            if (_logger.IsInfo) _logger.Info($"Disabling all data streams for deposit: '{depositId}'.");
            IEnumerable<Task<Keccak>> disableStreamTasks = from client in session.Clients
                select DisableDataStreamAsync(session.DepositId, client.Id);
            await Task.WhenAll(disableStreamTasks);
            if (_logger.IsInfo) _logger.Info($"Disabled all data streams for deposit: '{depositId}'.");

            return depositId;
        }
        
        private async Task<Keccak?> ToggleDataStreamAsync(Keccak depositId, bool enable, string client, string?[]? args = null)
        {
            ConsumerSession? session = _sessionService.GetActive(depositId);
            if (session is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Session for deposit: '{depositId}' was not found.");
                return null;
            }

            INdmPeer? provider = _providerService.GetPeer(session.ProviderAddress);
            if (provider is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider for address: '{session.ProviderAddress}' was not found.");
                return null;
            }
            
            DepositDetails? deposit = await _depositProvider.GetAsync(session.DepositId);
            if (deposit is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot toggle data stream, deposit: '{session.DepositId}' was not found.");

                return null;
            }
            
            if (!_wallet.IsUnlocked(deposit.Consumer))
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot toggle data stream for deposit: '{session.DepositId}', account: '{deposit.Consumer}' is locked.");

                return null;
            }
            
            Keccak dataAssetId = deposit.DataAsset.Id;
            DataAsset? dataAsset = _dataAssetService.GetDiscovered(dataAssetId);
            if (dataAsset is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset: '{dataAssetId}' was not found.");

                return null;
            }

            if (!_dataAssetService.IsAvailable(dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset: '{dataAssetId}' is unavailable, state: {dataAsset.State}.");

                return null;
            }

            if (!enable)
            {
                if (_logger.IsInfo) _logger.Info($"Sending disable data stream for deposit: '{depositId}', client: '{client}'.");
                provider.SendDisableDataStream(depositId, client);
                
                return depositId;
            }

            switch (dataAsset.QueryType)
            {
                case QueryType.Stream:
                {
                    if (session.GetClient(client)?.StreamEnabled == true)
                    {
                        if (_logger.IsInfo) _logger.Info($"Disabling an existing data stream for deposit: '{depositId}', client: '{client}'.");
                        provider.SendDisableDataStream(depositId, client);
                    }

                    if (_logger.IsInfo) _logger.Info($"Sending enable data stream for deposit: '{depositId}', client: '{client}'.");
                    break;
                }
                case QueryType.Query:
                {
                    Metrics.SentQueries++;
                    if (_logger.IsInfo) _logger.Info($"Sending the data query for deposit: '{depositId}', client: '{client}'.");
                    break;
                }
                default:
                {
                    throw new InvalidOperationException($"Not supported data asset type: {dataAsset.QueryType}.");
                }
            }

            provider.SendEnableDataStream(depositId, client, args ?? Array.Empty<string>());

            return depositId;
        }

        public async Task SetEnabledDataStreamAsync(Keccak depositId, string client, string?[] args)
        {
            ConsumerSession? session = _sessionService.GetActive(depositId);
            if (session is null)
            {
                return;
            }

            session.EnableStream(client, args);
            await _sessionRepository.UpdateAsync(session);
            await _consumerNotifier.SendDataStreamEnabledAsync(depositId, session.Id);
            if (_logger.IsInfo) _logger.Info($"Enabled data stream for deposit: '{depositId}', client: '{client}', session: '{session.Id}'.'");
        }

        public async Task SetDisabledDataStreamAsync(Keccak depositId, string client)
        {
            ConsumerSession? session = _sessionService.GetActive(depositId);
            if (session is null)
            {
                return;
            }
            
            session.DisableStream(client);
            await _sessionRepository.UpdateAsync(session);
            await _consumerNotifier.SendDataStreamDisabledAsync(depositId, session.Id);
            if (_logger.IsInfo) _logger.Info($"Disabled data stream for deposit: '{depositId}', client: '{client}', session: '{session.Id}'.");
        }
    }
}