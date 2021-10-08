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

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
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

        public ConsumerSession? GetActive(Keccak depositId)
        {
            if (_sessions.TryGetValue(depositId, out ConsumerSession? session))
            {
                return session;
            }

            if (_logger.IsWarn) _logger.Warn($"Active session for deposit: '{depositId}' was not found.");
            
            return null;
        }

        public IReadOnlyList<ConsumerSession> GetAllActive() => _sessions.Values.ToArray();

        public async Task StartSessionAsync(Session session, INdmPeer provider)
        {
            DepositDetails? deposit = await _depositProvider.GetAsync(session.DepositId);
            if (deposit is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}', deposit: '{session.DepositId}' was not found.");
                return;
            }
            
            if(session.StartTimestamp < deposit.ConfirmationTimestamp)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}', session timestamp {session.StartTimestamp} is before deposit confirmation timestamp {deposit.ConfirmationTimestamp}.");
                return;
            }

            Keccak dataAssetId = deposit.DataAsset.Id;
            if (dataAssetId != session.DataAssetId)
            {
                if (_logger.IsWarn) _logger.Warn($"Inconsistent data - data asset ID on deposit is '{dataAssetId}' while on session is '{session.DataAssetId}'.");
                return;
            }
            
            DataAsset? dataAsset = _dataAssetService.GetDiscovered(dataAssetId);
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
            
            if (session.ProviderAddress == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Session: '{session.Id}' for '{session.DepositId}' cannot be started because of the unknown provider address.");
                return;
            }

            if (!provider.ProviderAddress!.Equals(deposit.DataAsset.Provider.Address))
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot start the session: '{session.Id}' for deposit: '{session.DepositId}', provider address (peer): '{provider.ProviderAddress}' doesn't equal the address from data asset: '{deposit.DataAsset.Provider.Address}'.");
                return;
            }

            PagedResult<ConsumerSession> sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = session.DepositId,
                Results = int.MaxValue
            });
            
            uint consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            if (_logger.IsInfo) _logger.Info($"Starting the session: '{session.Id}' for deposit: '{session.DepositId}'. Settings consumed units - provider: {session.StartUnitsFromProvider}, consumer: {consumedUnits}.");
            ConsumerSession consumerSession = ConsumerSession.From(session);
            consumerSession.Start(session.StartTimestamp);
            ConsumerSession? previousSession = await _sessionRepository.GetPreviousAsync(consumerSession);
            uint upfrontUnits = (uint) (deposit.DataAsset.Rules.UpfrontPayment?.Value ?? 0);
            if (upfrontUnits > 0 && previousSession is null)
            {
                consumerSession.AddUnpaidUnits(upfrontUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: {upfrontUnits} for session: '{session.Id}' based on upfront payment.");
            }

            uint unpaidUnits = previousSession?.UnpaidUnits ?? 0;
            if (unpaidUnits > 0 && !(previousSession is null))
            {
                consumerSession.AddUnpaidUnits(unpaidUnits);
                if (_logger.IsInfo) _logger.Info($"Unpaid units: {unpaidUnits} for session: '{session.Id}' from previous session: '{previousSession.Id}'.");
            }
            
            if (deposit.DataAsset.UnitType == DataAssetUnitType.Time)
            {
                uint unpaidTimeUnits = (uint) consumerSession.StartTimestamp - deposit.ConfirmationTimestamp;
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
            
            Keccak depositId = session.DepositId;
            ConsumerSession? consumerSession = GetActive(depositId);
            if (consumerSession is null)
            {
                return;
            }
            
            _sessions.TryRemove(session.DepositId, out _);
            ulong timestamp = session.FinishTimestamp;
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

            ulong timestamp = _timestamper.UnixTime.Seconds;
            foreach ((Keccak _, ConsumerSession session) in _sessions)
            {
                if (!provider.ProviderAddress.Equals(session.ProviderAddress))
                {
                    continue;
                }

                Keccak depositId = session.DepositId;
                if (_logger.IsInfo) _logger.Info($"Finishing a session: '{session.Id}' for deposit: '{depositId}'.");
                _sessions.TryRemove(session.DepositId, out _);
                session.Finish(SessionState.ProviderDisconnected, timestamp);
                await _sessionRepository.UpdateAsync(session);
                await _consumerNotifier.SendSessionFinishedAsync(session.DepositId, session.Id);
                if (_logger.IsInfo) _logger.Info($"Finished a session: '{session.Id}' for deposit: '{depositId}', provider: '{provider.ProviderAddress}', state: '{session.State}', timestamp: {timestamp}.");
            }
        }

        public async Task<Keccak?> SendFinishSessionAsync(Keccak depositId)
        {
            DepositDetails? deposit = await _depositProvider.GetAsync(depositId);
            if (deposit is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' was not found.'");

                return null;
            }

            ConsumerSession? session = GetActive(depositId);
            if (session is null)
            {
                return null;
            }

            INdmPeer? provider = _providerService.GetPeer(session.ProviderAddress);
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
