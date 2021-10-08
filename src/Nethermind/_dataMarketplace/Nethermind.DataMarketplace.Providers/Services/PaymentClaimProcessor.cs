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

using System;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public class PaymentClaimProcessor : IPaymentClaimProcessor
    {
        private static readonly long Eth = (long) Unit.Ether;
        private readonly IGasPriceService _gasPriceService;
        private readonly IConsumerRepository _consumerRepository;
        private readonly IPaymentClaimRepository _paymentClaimRepository;
        private readonly IPaymentService _paymentService;
        private Address _coldWalletAddress;
        private readonly ITimestamper _timestamper;
        private readonly IRlpObjectDecoder<UnitsRange> _unitsRangeRlpDecoder;
        private readonly bool _disableSendingPaymentClaimTransaction;
        private readonly ILogger _logger;

        public PaymentClaimProcessor(IGasPriceService gasPriceService, IConsumerRepository consumerRepository,
            IPaymentClaimRepository paymentClaimRepository, IPaymentService paymentService, Address coldWalletAddress,
            ITimestamper timestamper, IRlpObjectDecoder<UnitsRange> unitsRangeRlpDecoder, ILogManager logManager,
            bool disableSendingPaymentClaimTransaction = false)
        {
            _gasPriceService = gasPriceService;
            _consumerRepository = consumerRepository;
            _paymentClaimRepository = paymentClaimRepository;
            _paymentService = paymentService;
            _coldWalletAddress = coldWalletAddress;
            _timestamper = timestamper;
            _unitsRangeRlpDecoder = unitsRangeRlpDecoder;
            _disableSendingPaymentClaimTransaction = disableSendingPaymentClaimTransaction;
            _logger = logManager.GetClassLogger();
        }

        public async Task<PaymentClaim?> ProcessAsync(DataDeliveryReceiptRequest receiptRequest, Signature signature)
        {
            Keccak depositId = receiptRequest.DepositId;
            Consumer? consumer = await _consumerRepository.GetAsync(depositId);
            if (consumer is null)
            {
                if(_logger.IsError) _logger.Error($"Could not find any consumers for deposit {depositId} in the repository.");
                return null;
            }
            
            DataRequest dataRequest = consumer.DataRequest;
            UnitsRange range = receiptRequest.UnitsRange;
            uint claimedUnits = range.Units;
            BigInteger claimedValue = claimedUnits * (BigInteger) consumer.DataRequest.Value / consumer.DataRequest.Units;
            ulong timestamp = _timestamper.UnixTime.Seconds;
            Rlp unitsRangeRlp = _unitsRangeRlpDecoder.Encode(range);
            Keccak id = Keccak.Compute(Rlp.Encode(Rlp.Encode(depositId), Rlp.Encode(timestamp), unitsRangeRlp).Bytes);
            PaymentClaim paymentClaim = new PaymentClaim(id, consumer.DepositId, consumer.DataAsset.Id,
                consumer.DataAsset.Name, dataRequest.Units, claimedUnits, range, dataRequest.Value,
                (UInt256) claimedValue, dataRequest.ExpiryTime, dataRequest.Pepper, dataRequest.Provider,
                dataRequest.Consumer, signature, timestamp, Array.Empty<TransactionInfo>(), PaymentClaimStatus.Unknown);
            await _paymentClaimRepository.AddAsync(paymentClaim);
            if (_logger.IsInfo)_logger.Info($"Claiming a payment (id: '{paymentClaim.Id}') for deposit: '{depositId}', range: [{range.From}, {range.To}], units: {claimedUnits}.");
            UInt256 gasPrice = await _gasPriceService.GetCurrentPaymentClaimGasPriceAsync();
            Keccak? transactionHash = null;
            if (_disableSendingPaymentClaimTransaction)
            {
                if (_logger.IsWarn) _logger.Warn("*** NDM provider sending payment claim transaction is disabled ***");
            }
            else
            {
                transactionHash = await SendTransactionAsync(paymentClaim, gasPrice);
                if (transactionHash is null)
                {
                    if (_logger.IsInfo) _logger.Info($"Payment claim (id: {paymentClaim.Id}) for deposit: '{paymentClaim.DepositId}' did not receive a transaction hash.");
                    return paymentClaim;
                }
            }
            
            if (_logger.IsInfo) _logger.Info($"Payment claim (id: {paymentClaim.Id}) for deposit: '{paymentClaim.DepositId}' received a transaction hash: '{transactionHash}'.");
            paymentClaim.AddTransaction(TransactionInfo.Default(transactionHash, 0, gasPrice, _paymentService.GasLimit,
                timestamp));
            paymentClaim.SetStatus(PaymentClaimStatus.Sent);
            await _paymentClaimRepository.UpdateAsync(paymentClaim);
            string claimedEth  = ((decimal) claimedValue / Eth).ToString("0.0000");
            if (_logger.IsInfo) _logger.Info($"Sent a payment claim (id: '{paymentClaim.Id}') for deposit: '{depositId}', range: [{range.From}, {range.To}], units: {claimedUnits}, units: {claimedUnits}, value: {claimedValue} wei ({claimedEth} ETH, transaction hash: '{transactionHash}'.");

            return paymentClaim;
        }

        public async Task<Keccak?> SendTransactionAsync(PaymentClaim paymentClaim, UInt256 gasPrice)
        {
            bool isPaymentClaimCorrect = IsPaymentClaimCorrect(paymentClaim);
            if(!isPaymentClaimCorrect)
            {
                if(_logger.IsWarn)
                {
                    _logger.Warn($"Payment claim id: {paymentClaim.Id} was incorrect. Claim will be rejected");
                }

                paymentClaim.Reject();
                await _paymentClaimRepository.UpdateAsync(paymentClaim);

                return null;
            }

            Keccak depositId = paymentClaim.DepositId;
            UnitsRange range = paymentClaim.UnitsRange;
            Keccak? transactionHash = await _paymentService.ClaimPaymentAsync(paymentClaim, _coldWalletAddress, gasPrice);
            bool isTransactionHashValid = !(transactionHash is null) && transactionHash != Keccak.Zero;
            if (isTransactionHashValid)
            {
                if (_logger.IsInfo)_logger.Info($"Received a transaction hash: {transactionHash} for payment claim (id: '{paymentClaim.Id}') for deposit: '{depositId}', range: [{range.From}, {range.To}], units: {paymentClaim.Units}.");
                return transactionHash;
            }
            
            if (_logger.IsError) _logger.Error($"There was an error when claiming a payment (id: '{paymentClaim.Id}') for deposit: '{depositId}', range: [{range.From}, {range.To}] with receipt [{range.From}, {range.To}], units: {paymentClaim.Units} - returned transaction is empty.");

            return null;
        }

        public void ChangeColdWalletAddress(Address address)
        {
            _coldWalletAddress = address;
        }

        private bool IsPaymentClaimCorrect(PaymentClaim paymentClaim)
        {
            var from = paymentClaim.UnitsRange.From;
            var to = paymentClaim.UnitsRange.To;

            if(from > to)
            {
                if(_logger.IsInfo)
                {
                    _logger.Info($"Invalid units range for transaction. Units range: [{from},{to}]");
                }
                return false;
            }

            if(to >= paymentClaim.Units)
            {
                if(_logger.IsInfo)
                {
                    _logger.Info($"Invalid units range for transaction. UnitsRange.To cannot be higher or equal to payment claim units. UnitRange: [{from},{to}], Units: {paymentClaim.Units}");
                }
                return false;
            }

            return true;
        }
    }
}