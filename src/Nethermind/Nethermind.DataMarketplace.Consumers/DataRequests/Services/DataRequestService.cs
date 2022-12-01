// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            if (deposit.IsExpired((uint)_timestamper.UnixTime.Seconds))
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
            var consumedUnits = sessions.Items.Any() ? (uint)sessions.Items.Sum(s => s.ConsumedUnits) : 0;
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
