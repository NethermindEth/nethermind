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

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.DataRequests.Services
{
    public class DataRequestService : IDataRequestService
    {
        private readonly IDataRequestFactory _dataRequestFactory;
        private readonly IDepositProvider _depositProvider;
        private readonly IKycVerifier _kycVerifier;
        private readonly IWallet _wallet;
        private readonly IProviderService _providerService;
        private readonly ITimestamper _timestamper;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly ILogger _logger;

        public DataRequestService(IDataRequestFactory dataRequestFactory, IDepositProvider depositProvider,
            IKycVerifier kycVerifier, IWallet wallet, IProviderService providerService, ITimestamper timestamper,
            IConsumerSessionRepository sessionRepository, IConsumerNotifier consumerNotifier, ILogManager logManager)
        {
            _dataRequestFactory = dataRequestFactory;
            _depositProvider = depositProvider;
            _kycVerifier = kycVerifier;
            _wallet = wallet;
            _providerService = providerService;
            _timestamper = timestamper;
            _sessionRepository = sessionRepository;
            _consumerNotifier = consumerNotifier;
            _logger = logManager.GetClassLogger();
        }

        public async Task<DataRequestResult> SendAsync(Keccak depositId)
        {
            var deposit = await _depositProvider.GetAsync(depositId);
            if (deposit is null)
            {
                return DataRequestResult.DepositNotFound;
            }

            if (!_wallet.IsUnlocked(deposit.Consumer))
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{deposit.Consumer}' is locked, can't send a data request.");

                return DataRequestResult.ConsumerAccountLocked;
            }

            if (deposit.DataAsset.KycRequired &&
                !(await _kycVerifier.IsVerifiedAsync(deposit.DataAsset.Id, deposit.Consumer)))
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' has unconfirmed KYC.'");

                return DataRequestResult.KycUnconfirmed;
            }

            if (!deposit.Confirmed)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' is unconfirmed.'");

                return DataRequestResult.DepositUnconfirmed;
            }

            if (deposit.IsExpired((uint) _timestamper.UnixTime.Seconds))
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit with id: '{depositId}' is expired.'");

                return DataRequestResult.DepositExpired;
            }

            var providerPeer = _providerService.GetPeer(deposit.DataAsset.Provider.Address);
            if (providerPeer is null)
            {
                return DataRequestResult.ProviderNotFound;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = depositId,
                Results = int.MaxValue
            });
            var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            var dataRequest = CreateDataRequest(deposit);
            if (_logger.IsInfo) _logger.Info($"Sending data request for deposit with id: '{depositId}', consumed units: {consumedUnits}, address: '{dataRequest.Consumer}'.");
            var result = await providerPeer.SendDataRequestAsync(dataRequest, consumedUnits);
            if (_logger.IsInfo) _logger.Info($"Received data request result: '{result}' for data asset: '{dataRequest.DataAssetId}', deposit: '{depositId}', consumed units: {consumedUnits}, address: '{dataRequest.Consumer}'.");
            await _consumerNotifier.SendDataRequestResultAsync(depositId, result);
            
            return result;
        }
        
        private DataRequest CreateDataRequest(DepositDetails deposit)
            => _dataRequestFactory.Create(deposit.Deposit, deposit.DataAsset.Id, deposit.DataAsset.Provider.Address,
                deposit.Consumer, deposit.Pepper);
    }
}
