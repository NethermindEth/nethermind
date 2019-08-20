/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Sessions.Services
{
    public class SessionService : ISessionService
    {
        private readonly IProviderService _providerService;
        private readonly IDepositProvider _depositProvider;
        private readonly IDataAssetService _dataAssetService;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly ITimestamper _timestamper;
        private readonly IConsumerNotifier _consumerNotifier;

        private readonly ConcurrentDictionary<Keccak, ConsumerSession> _sessions =
            new ConcurrentDictionary<Keccak, ConsumerSession>();

        private readonly ILogger _logger;

        public SessionService(IProviderService providerService, IDepositProvider depositProvider,
            IDataAssetService dataAssetService, IConsumerSessionRepository sessionRepository, ITimestamper timestamper,
            IConsumerNotifier consumerNotifier, ILogManager logManager)
        {
            _providerService = providerService;
            _depositProvider = depositProvider;
            _dataAssetService = dataAssetService;
            _sessionRepository = sessionRepository;
            _timestamper = timestamper;
            _consumerNotifier = consumerNotifier;
            _logger = logManager.GetClassLogger();
        }

        public ConsumerSession GetActive(Keccak depositId)
        {
            if (_sessions.TryGetValue(depositId, out var session))
            {
                return session;
            }

            if (_logger.IsWarn) _logger.Warn($"Active session for deposit: '{depositId}' was not found.");
            
            return null;
        }

        public IReadOnlyList<ConsumerSession> GetAllActive()
            => _sessions.Values.ToArray();

        public async Task StartSessionAsync(Session session, INdmPeer provider)
        {
//            var providerPeer = _providerService.GetPeer(provider.ProviderAddress);
//            if (!_providers.TryGetValue(provider.NodeId, out var providerPeer))
//            {
//                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}', provider: '{provider.NodeId}' was not found.");
//
//                return;
//            }

            var deposit = await _depositProvider.GetAsync(session.DepositId);
            if (deposit is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}', deposit: '{session.DepositId}' was not found.");

                return;
            }

            var dataAssetId = deposit.DataAsset.Id;
            var dataAsset = _dataAssetService.GetDiscovered(dataAssetId);
            if (dataAsset is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Available data asset: '{dataAssetId}' was not found.");

                return;
            }

            if (!_dataAssetService.IsAvailable(dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset: '{dataAssetId}' is unavailable, state: {dataAsset.State}.");

                return;
            }

            if (!provider.ProviderAddress.Equals(deposit.DataAsset.Provider.Address))
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}' for deposit: '{session.DepositId}', provider address (peer): '{provider.ProviderAddress}' doesn't equal the address from data asset: '{deposit.DataAsset.Provider.Address}'.");

                return;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = session.DepositId,
                Results = int.MaxValue
            });
            var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            if (_logger.IsInfo) _logger.Info($"Starting the session: '{session.Id}' for deposit: '{session.DepositId}'. Settings consumed units - provider: {session.StartUnitsFromProvider}, consumer: {consumedUnits}.");
            var consumerSession = ConsumerSession.From(session);
            consumerSession.Start(session.StartTimestamp);
            var previousSession = await _sessionRepository.GetPreviousAsync(consumerSession);
            var upfrontUnits = (uint) (deposit.DataAsset.Rules.UpfrontPayment?.Value ?? 0);
            if (upfrontUnits > 0 && previousSession is null)
            {
                consumerSession.AddUnpaidUnits(upfrontUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: {upfrontUnits} for session: '{session.Id}' based on upfront payment.");
            }

            var unpaidUnits = previousSession?.UnpaidUnits ?? 0;
            if (unpaidUnits > 0 && !(previousSession is null))
            {
                consumerSession.AddUnpaidUnits(unpaidUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: {unpaidUnits} for session: '{session.Id}' from previous session: '{previousSession.Id}'.");
            }
            
            if (deposit.DataAsset.UnitType == DataAssetUnitType.Time)
            {
                var unpaidTimeUnits = (uint) consumerSession.StartTimestamp - deposit.ConfirmationTimestamp;
                consumerSession.AddUnpaidUnits(unpaidTimeUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: '{unpaidTimeUnits}' for deposit: '{session.DepositId}' based on time.");
            }

            SetActiveSession(consumerSession);
            await _sessionRepository.AddAsync(consumerSession);
            await _consumerNotifier.SendSessionStartedAsync(session.DepositId, session.Id);
            if (_logger.IsInfo) _logger.Info($"Started a session with id: '{session.Id}' for deposit: '{session.DepositId}', address: '{deposit.Consumer}'.");
        }
        
        public async Task FinishSessionAsync(Session session, INdmPeer provider, bool removePeer = true)
        {
            if (removePeer)
            {
                _providerService.Remove(provider.NodeId);
            }

            if (provider.ProviderAddress is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider node: '{provider.NodeId}' has no address assigned.");
                return;
            }
            
            var depositId = session.DepositId;
            var consumerSession = GetActive(depositId);
            if (consumerSession is null)
            {
                return;
            }
            
            _sessions.TryRemove(session.DepositId, out _);
            var timestamp = session.FinishTimestamp;
            consumerSession.Finish(session.State, timestamp);
            await _sessionRepository.UpdateAsync(consumerSession);
            await _consumerNotifier.SendSessionFinishedAsync(session.DepositId, session.Id);
            if (_logger.IsInfo) _logger.Info($"Finished a session: '{session.Id}' for deposit: '{depositId}', provider: '{provider.ProviderAddress}', state: '{session.State}', timestamp: {timestamp}.");
        }
        
        public async Task FinishSessionsAsync(INdmPeer provider, bool removePeer = true)
        {
            if (_logger.IsInfo) _logger.Info($"Finishing {_sessions.Count} session(s) with provider: '{provider.ProviderAddress}'.");
            if (removePeer)
            {
                _providerService.Remove(provider.NodeId);
            }
            
            if (provider.ProviderAddress is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider node: '{provider.NodeId}' has no address assigned.");
                return;
            }

            var timestamp = _timestamper.EpochSeconds;
            foreach (var (_, session) in _sessions)
            {
                if (!provider.ProviderAddress.Equals(session.ProviderAddress))
                {
                    if (_logger.IsInfo) _logger.Info($"Provider: '{provider.ProviderAddress}' address is invalid.");

                    continue;
                }

                var depositId = session.DepositId;
                if (_logger.IsInfo) _logger.Info($"Finishing a session: '{session.Id}' for deposit: '{depositId}'.");
                _sessions.TryRemove(session.DepositId, out _);
                session.Finish(SessionState.ProviderDisconnected, timestamp);
                await _sessionRepository.UpdateAsync(session);
                await _consumerNotifier.SendSessionFinishedAsync(session.DepositId, session.Id);
                if (_logger.IsInfo) _logger.Info($"Finished a session: '{session.Id}' for deposit: '{depositId}', provider: '{provider.ProviderAddress}', state: '{session.State}', timestamp: {timestamp}.");
            }
        }

        public async Task<Keccak> SendFinishSessionAsync(Keccak depositId)
        {
            var deposit = await _depositProvider.GetAsync(depositId);
            if (deposit is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' was not found.'");

                return null;
            }

            var session = GetActive(depositId);
            if (session is null)
            {
                return null;
            }

            var provider = _providerService.GetPeer(session.ProviderAddress);
            if (provider is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider node: '{session.ProviderNodeId}' was not found.");
                
                return null;
            }

            provider.SendFinishSession(depositId);

            return depositId;
        }

        private void SetActiveSession(ConsumerSession session)
        {
            _sessions.TryRemove(session.DepositId, out _);            
            _sessions.TryAdd(session.DepositId, session);
        }
    }
}