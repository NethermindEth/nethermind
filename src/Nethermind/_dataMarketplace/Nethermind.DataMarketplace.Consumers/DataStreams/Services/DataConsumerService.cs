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

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.DataStreams.Services
{
    public class DataConsumerService : IDataConsumerService
    {
        private readonly IDepositProvider _depositProvider;
        private readonly ISessionService _sessionService;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly ITimestamper _timestamper;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly ILogger _logger;

        public DataConsumerService(IDepositProvider depositProvider, ISessionService sessionService, 
            IConsumerNotifier consumerNotifier, ITimestamper timestamper, IConsumerSessionRepository sessionRepository,
            ILogManager logManager)
        {
            _depositProvider = depositProvider;
            _sessionService = sessionService;
            _consumerNotifier = consumerNotifier;
            _timestamper = timestamper;
            _sessionRepository = sessionRepository;
            _logger = logManager.GetClassLogger();
        }
        
        public async Task SetUnitsAsync(Keccak depositId, uint consumedUnitsFromProvider)
        {
            var (session, deposit) = await TryGetSessionAndDepositAsync(depositId);
            if (session is null || deposit is null)
            {
                return;
            }
            
            session.SetConsumedUnitsFromProvider(consumedUnitsFromProvider);
            switch (deposit.DataAsset.UnitType)
            {
                case DataAssetUnitType.Time:
                    var now = (uint) _timestamper.UnixTime.Seconds;
                    var currentlyConsumedUnits = now - deposit.ConfirmationTimestamp;
                    var currentlyUnpaidUnits = currentlyConsumedUnits > session.PaidUnits
                        ? currentlyConsumedUnits - session.PaidUnits
                        : 0;
                    session.SetConsumedUnits((uint)(now - session.StartTimestamp));
                    session.SetUnpaidUnits(currentlyUnpaidUnits);
                    break;
                case DataAssetUnitType.Unit:
                    session.IncrementConsumedUnits();
                    session.IncrementUnpaidUnits();
                    Metrics.ConsumedUnits++;
                    break;
            }
            
            var consumedUnits = session.ConsumedUnits;
            var unpaidUnits = session.UnpaidUnits;
            if (_logger.IsTrace) _logger.Trace($"Setting units, consumed: [provider: {consumedUnitsFromProvider}, consumer: {consumedUnits}], unpaid: {unpaidUnits}, paid: {session.PaidUnits}, for deposit: '{depositId}', session: '{session.Id}'.");
            if (consumedUnitsFromProvider > consumedUnits)
            {
                var unitsDifference = consumedUnitsFromProvider - consumedUnits;
                if (_logger.IsTrace) _logger.Trace($"Provider has counted more consumed units ({unitsDifference}) for deposit: '{depositId}', session: '{session.Id}'");
            }
            else if (consumedUnitsFromProvider < consumedUnits)
            {
                var unitsDifference = consumedUnits - consumedUnitsFromProvider;
                if (_logger.IsTrace) _logger.Trace($"Provider has counted less consumed units ({unitsDifference}) for deposit: '{depositId}', session: '{session.Id}'.");
                
                //Adjust units?
//                session.SubtractUnpaidUnits(unpaidUnits);
//                session.SubtractUnpaidUnits(unitsDifference);
            }

            
            await _sessionRepository.UpdateAsync(session);
        }

        public async Task SetDataAvailabilityAsync(Keccak depositId, DataAvailability dataAvailability)
        {
            var session = _sessionService.GetActive(depositId);
            if (session is null)
            {
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"Setting data availability: '{dataAvailability}', for deposit: '{depositId}', session: {session.Id}.");
            session.SetDataAvailability(dataAvailability);
            await _sessionRepository.UpdateAsync(session);
            await _consumerNotifier.SendDataAvailabilityChangedAsync(depositId, session.Id, dataAvailability);
        }

        public async Task HandleInvalidDataAsync(Keccak depositId, InvalidDataReason reason)
        {
            var session = _sessionService.GetActive(depositId);
            if (session is null)
            {
                return;
            }

            await _consumerNotifier.SendDataInvalidAsync(depositId, reason);

        }
        public async Task HandleGraceUnitsExceededAsync(Keccak depositId, uint consumedUnitsFromProvider, uint graceUnits)
        {
            if (_logger.IsWarn) _logger.Warn($"Handling the exceeded grace units for deposit: {depositId} (consumed: {consumedUnitsFromProvider}, grace: {graceUnits}).");
            var (session, deposit) = await TryGetSessionAndDepositAsync(depositId);
            if (session is null || deposit is null)
            {
                return;
            }

            var consumedUnits = session.ConsumedUnits;
            if (_logger.IsWarn) _logger.Warn($"Exceeded the grace units for deposit: {depositId}, consumed: {consumedUnits} (provider claims: {consumedUnitsFromProvider}), grace: {graceUnits}.");
            await _consumerNotifier.SendGraceUnitsExceeded(depositId, deposit.DataAsset.Name,
                consumedUnitsFromProvider, consumedUnits, graceUnits);
        }
        
        private async Task<(ConsumerSession? session, DepositDetails? deposit)> TryGetSessionAndDepositAsync(
            Keccak depositId)
        {
            var session = _sessionService.GetActive(depositId);
            if (session is null)
            {
                if (_logger.IsInfo) _logger.Info($"Session for deposit: '{depositId}' was not found.");

                return (null, null);
            }
            
            var deposit = await _depositProvider.GetAsync(depositId);
            if (deposit is null)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit: '{depositId}' was not found.");

                return (session, null);
            }
                
            return (session, deposit);
        }
    }
}
