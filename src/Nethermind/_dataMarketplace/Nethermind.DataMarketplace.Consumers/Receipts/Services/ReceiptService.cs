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
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Consumers.Sessions;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.Receipts.Services
{
    public class ReceiptService : IReceiptService
    {
        private readonly AbiSignature _dataDeliveryReceiptAbiSig = new AbiSignature("dataDeliveryReceipt",
            new AbiBytes(32),
            new AbiFixedLengthArray(new AbiUInt(32), 2));
        
        private readonly IDepositProvider _depositProvider;
        private readonly IProviderService _providerService;
        private readonly IReceiptRequestValidator _receiptRequestValidator;
        private readonly ISessionService _sessionService;
        private readonly ITimestamper _timestamper;
        private readonly IReceiptRepository _receiptRepository;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly IAbiEncoder _abiEncoder;
        private readonly IWallet _wallet;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly PublicKey _nodePublicKey;
        private readonly ILogger _logger;
        
        public ReceiptService(IDepositProvider depositProvider, IProviderService providerService,
            IReceiptRequestValidator receiptRequestValidator, ISessionService sessionService, ITimestamper timestamper,
            IReceiptRepository receiptRepository, IConsumerSessionRepository sessionRepository, IAbiEncoder abiEncoder,
            IWallet wallet, IEthereumEcdsa ecdsa, PublicKey nodePublicKey, ILogManager logManager)
        {
            _depositProvider = depositProvider;
            _providerService = providerService;
            _receiptRequestValidator = receiptRequestValidator;
            _sessionService = sessionService;
            _timestamper = timestamper;
            _receiptRepository = receiptRepository;
            _sessionRepository = sessionRepository;
            _abiEncoder = abiEncoder;
            _wallet = wallet;
            _ecdsa = ecdsa;
            _nodePublicKey = nodePublicKey;
            _logger = logManager.GetClassLogger();
        }
        
        public async Task SendAsync(DataDeliveryReceiptRequest request, int fetchSessionRetries = 3,
            int fetchSessionRetryDelayMilliseconds = 3000)
        {
            var depositId = request.DepositId;
            var (deposit, session) = await TryGetDepositAndSessionAsync(depositId,
                fetchSessionRetries, fetchSessionRetryDelayMilliseconds);
            if (deposit is null || session is null)
            {
                return;
            }

            var providerAddress = deposit.DataAsset.Provider.Address;
            var providerPeer = _providerService.GetPeer(providerAddress);
            if (providerPeer is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider: '{providerAddress}' was not found.");

                return;
            }

            var receiptId = Keccak.Compute(Rlp.Encode(Rlp.Encode(depositId), Rlp.Encode(request.Number),
                Rlp.Encode(_timestamper.UnixTime.Seconds)).Bytes);
            if (!_receiptRequestValidator.IsValid(request, session.UnpaidUnits, session.ConsumedUnits,
                deposit.Deposit.Units))
            {
                if (_logger.IsWarn) _logger.Warn($"Provider: '{providerPeer.NodeId}' sent an invalid data delivery receipt request.");
                var receipt = new DataDeliveryReceipt(StatusCodes.InvalidReceiptRequestRange,
                    session.ConsumedUnits, session.UnpaidUnits, new Signature(1, 1, 27));
                await _receiptRepository.AddAsync(new DataDeliveryReceiptDetails(receiptId, session.Id,
                    session.DataAssetId, _nodePublicKey, request, receipt, _timestamper.UnixTime.Seconds, false));
                await _sessionRepository.UpdateAsync(session);
                providerPeer.SendDataDeliveryReceipt(depositId, receipt);
                return;
            }

            var abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _dataDeliveryReceiptAbiSig,
                depositId.Bytes, new[] {request.UnitsRange.From, request.UnitsRange.To});
            var receiptHash = Keccak.Compute(abiHash);
            var signature = _wallet.Sign(receiptHash, deposit.Consumer);
            var recoveredAddress = _ecdsa.RecoverPublicKey(signature, receiptHash)?.Address;
            if (deposit.Consumer != recoveredAddress)
            {
                if (_logger.IsError) _logger.Error($"Signature failure when signing the receipt from provider: '{providerPeer.NodeId}', invalid recovered address.");
                var receipt = new DataDeliveryReceipt(StatusCodes.InvalidReceiptAddress,
                    session.ConsumedUnits, session.UnpaidUnits, new Signature(1, 1, 27));
                await _receiptRepository.AddAsync(new DataDeliveryReceiptDetails(receiptId, session.Id,
                    session.DataAssetId, _nodePublicKey, request, receipt, _timestamper.UnixTime.Seconds, false));
                await _sessionRepository.UpdateAsync(session);
                providerPeer.SendDataDeliveryReceipt(depositId, receipt);
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Provider: '{providerPeer.NodeId}' sent a valid data delivery receipt request.");
            if (request.ReceiptsToMerge.Any())
            {
                if (_logger.IsInfo) _logger.Info($"Processing a merged receipt request for deposit: {session.DepositId}, session: '{session.Id} - units will not be updated.");
            }
            else
            {
                var paidUnits = request.UnitsRange.To - request.UnitsRange.From + 1;
                var unpaidUnits = session.UnpaidUnits > paidUnits ? session.UnpaidUnits - paidUnits : 0;
                session.SetUnpaidUnits(unpaidUnits);
                session.AddPaidUnits(paidUnits);
                if (request.IsSettlement)
                {
                    session.SetPaidUnits(paidUnits);
                    session.SettleUnits(paidUnits);
                    if (_logger.IsInfo) _logger.Info($"Settled {paidUnits} units for deposit: '{session.DepositId}', session: '{session.Id}'.");
                }
                
                await _sessionRepository.UpdateAsync(session);
            }
            
            if (_logger.IsInfo) _logger.Info($"Sending data delivery receipt for deposit: '{depositId}', range: [{request.UnitsRange.From}, {request.UnitsRange.To}].");
            var deliveryReceipt = new DataDeliveryReceipt(StatusCodes.Ok, session.ConsumedUnits,
                session.UnpaidUnits, signature);
            await _receiptRepository.AddAsync(new DataDeliveryReceiptDetails(receiptId, session.Id,
                session.DataAssetId, _nodePublicKey, request, deliveryReceipt, _timestamper.UnixTime.Seconds, false));
            providerPeer.SendDataDeliveryReceipt(depositId, deliveryReceipt);
            if (_logger.IsInfo) _logger.Info($"Sent data delivery receipt for deposit: '{depositId}', range: [{request.UnitsRange.From}, {request.UnitsRange.To}].");
        }

        private async Task<(DepositDetails? deposit, ConsumerSession? session)> TryGetDepositAndSessionAsync(
            Keccak depositId, int fetchSessionRetries = 5, int fetchSessionRetryDelayMilliseconds = 3000)
        {
            DepositDetails? deposit = await _depositProvider.GetAsync(depositId);
            if (deposit is null)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit: '{depositId}' was not found.");

                return (null, null);
            }

            var session = _sessionService.GetActive(depositId);
            if (!(session is null))
            {
                return (deposit, session);
            }
            
            if (fetchSessionRetries <= 0)
            {
                return (deposit, null);
            }
            
            var retry = 0;
            while (retry < fetchSessionRetries)
            {
                retry++;
                if (_logger.IsTrace) _logger.Trace($"Retrying ({retry}/{fetchSessionRetries}) fetching an active session for deposit: {deposit} in {fetchSessionRetryDelayMilliseconds} ms...");
                await Task.Delay(fetchSessionRetryDelayMilliseconds);
                session = _sessionService.GetActive(depositId);
                if (session is null)
                {
                    continue;
                }
                if (_logger.IsInfo) _logger.Info($"Found an active session: '{session.Id}' for deposit: '{deposit.Id}'.");
                break;
            }
            
            return (session is null) ? (deposit, null) : (deposit, session);
        }
    }
}
