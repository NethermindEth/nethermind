// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.Refunds.Services
{
    public class RefundService : IRefundService
    {
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly IAbiEncoder _abiEncoder;
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly Address _contractAddress;
        private readonly ILogger _logger;
        private readonly IWallet _wallet;

        public RefundService(INdmBlockchainBridge blockchainBridge, IAbiEncoder abiEncoder,
            IDepositDetailsRepository depositRepository, Address contractAddress, ILogManager logManager,
            IWallet wallet)
        {
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _depositRepository = depositRepository;
            _contractAddress = contractAddress ?? throw new ArgumentNullException(nameof(contractAddress));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public ulong GasLimit { get; } = 55000;

        public async Task SetEarlyRefundTicketAsync(EarlyRefundTicket ticket, RefundReason reason)
        {
            var depositDetails = await _depositRepository.GetAsync(ticket.DepositId);
            if (depositDetails is null)
            {
                return;
            }

            depositDetails.SetEarlyRefundTicket(ticket);
            await _depositRepository.UpdateAsync(depositDetails);
            if (_logger.IsInfo) _logger.Info($"Early refund claim for deposit: '{ticket.DepositId}', reason: '{reason}'.");
        }

        public async Task<Keccak?> ClaimRefundAsync(Address onBehalfOf, RefundClaim refundClaim, UInt256 gasPrice)
        {
            byte[] txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractData.ClaimRefundSig,
                refundClaim.AssetId.Bytes, refundClaim.Units, refundClaim.Value, refundClaim.ExpiryTime,
                refundClaim.Pepper, refundClaim.Provider, onBehalfOf);
            Transaction transaction = new Transaction();
            transaction.Value = 0;
            transaction.Data = txData;
            transaction.To = _contractAddress;
            transaction.SenderAddress = onBehalfOf;
            transaction.GasLimit = (long)GasLimit;
            transaction.GasPrice = gasPrice;
            transaction.Nonce = await _blockchainBridge.GetNonceAsync(onBehalfOf);

            _wallet.Sign(transaction, await _blockchainBridge.GetNetworkIdAsync());

            if (_logger.IsInfo)
            {
                _logger.Info($"Sending a refund claim transaction for {refundClaim.DepositId} to be refunded to {refundClaim.RefundTo}");
            }

            return await _blockchainBridge.SendOwnTransactionAsync(transaction);
        }

        public async Task<Keccak?> ClaimEarlyRefundAsync(Address onBehalfOf, EarlyRefundClaim earlyRefundClaim,
            UInt256 gasPrice)
        {
            byte[] txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractData.ClaimEarlyRefundSig,
                earlyRefundClaim.AssetId.Bytes, earlyRefundClaim.Units, earlyRefundClaim.Value,
                earlyRefundClaim.ExpiryTime, earlyRefundClaim.Pepper, earlyRefundClaim.Provider,
                earlyRefundClaim.ClaimableAfter, earlyRefundClaim.Signature.V, earlyRefundClaim.Signature.R,
                earlyRefundClaim.Signature.S, onBehalfOf);
            Transaction transaction = new Transaction();
            transaction.Value = 0;
            transaction.Data = txData;
            transaction.To = _contractAddress;
            transaction.SenderAddress = onBehalfOf;
            transaction.GasLimit = (long)GasLimit;
            transaction.GasPrice = gasPrice;
            transaction.Nonce = await _blockchainBridge.GetNonceAsync(onBehalfOf);

            _wallet.Sign(transaction, await _blockchainBridge.GetNetworkIdAsync());

            if (_logger.IsInfo)
            {
                _logger.Info($"Sending an early refund claim transaction on {earlyRefundClaim.DepositId} to be refunded to {earlyRefundClaim.RefundTo}");
            }

            return await _blockchainBridge.SendOwnTransactionAsync(transaction);
        }
    }
}
