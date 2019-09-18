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

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Refunds.Services
{
    public class RefundClaimant : IRefundClaimant
    {
        private readonly IRefundService _refundService;
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly ITransactionVerifier _transactionVerifier;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        public RefundClaimant(IRefundService refundService, INdmBlockchainBridge blockchainBridge,
            IDepositDetailsRepository depositRepository, ITransactionVerifier transactionVerifier,
            ITimestamper timestamper, ILogManager logManager)
        {
            _refundService = refundService;
            _blockchainBridge = blockchainBridge;
            _depositRepository = depositRepository;
            _transactionVerifier = transactionVerifier;
            _timestamper = timestamper;
            _logger = logManager.GetClassLogger();
        }

        public async Task TryClaimRefundAsync(DepositDetails deposit, Address refundTo)
        {
            var now = _timestamper.EpochSeconds;
            if (!deposit.CanClaimRefund(now))
            {
                return;
            }
            
            var latestBlock = await _blockchainBridge.GetLatestBlockAsync();
            now = (ulong) latestBlock.Timestamp;
            if (!deposit.CanClaimRefund(now))
            {
                return;
            }
            
            var depositId = deposit.Deposit.Id;
            var transactionHash = deposit.ClaimedRefundTransactionHash;
            if (transactionHash is null)
            {
                var provider = deposit.DataAsset.Provider.Address;
                var refundClaim = new RefundClaim(depositId, deposit.DataAsset.Id, deposit.Deposit.Units,
                    deposit.Deposit.Value, deposit.Deposit.ExpiryTime, deposit.Pepper, provider, refundTo);
                transactionHash = await _refundService.ClaimRefundAsync(refundTo, refundClaim);
                deposit.SetClaimedRefundTransactionHash(transactionHash);
                await _depositRepository.UpdateAsync(deposit);
                if (_logger.IsInfo) _logger.Info($"Claimed a refund for deposit: '{depositId}', transaction hash: '{transactionHash}' (awaits a confirmation).");
            }

            await TryConfirmClaimAsync(deposit, string.Empty);
        }

        public async Task TryClaimEarlyRefundAsync(DepositDetails deposit, Address refundTo)
        {
            var now = _timestamper.EpochSeconds;
            if (!deposit.CanClaimEarlyRefund(now))
            {
                return;
            }
            
            var latestBlock = await _blockchainBridge.GetLatestBlockAsync();
            now = (ulong) latestBlock.Timestamp;
            if (!deposit.CanClaimEarlyRefund(now))
            {
                return;
            }
            
            var depositId = deposit.Deposit.Id;
            var transactionHash = deposit.ClaimedRefundTransactionHash;
            if (transactionHash is null)
            {
                var provider = deposit.DataAsset.Provider.Address;
                var ticket = deposit.EarlyRefundTicket;
                var earlyRefundClaim = new EarlyRefundClaim(ticket.DepositId, deposit.DataAsset.Id,
                    deposit.Deposit.Units, deposit.Deposit.Value, deposit.Deposit.ExpiryTime, deposit.Pepper, provider,
                    ticket.ClaimableAfter, ticket.Signature, refundTo);
                transactionHash = await _refundService.ClaimEarlyRefundAsync(refundTo, earlyRefundClaim);
                deposit.SetClaimedRefundTransactionHash(transactionHash);
                await _depositRepository.UpdateAsync(deposit);
                if (_logger.IsInfo) _logger.Info($"Claimed an early refund for deposit: '{depositId}', transaction hash: '{transactionHash}' (awaits a confirmation).");
            }

            await TryConfirmClaimAsync(deposit, "early ");
        }

        private async Task TryConfirmClaimAsync(DepositDetails deposit, string type)
        {
            var depositId = deposit.Id;
            var transactionHash = deposit.TransactionHash;
            var transaction  = await _blockchainBridge.GetTransactionAsync(transactionHash);          
            if (transaction is null)
            {
                if (_logger.IsInfo) _logger.Info($"Transaction was not found for hash: '{transactionHash}' for deposit: '{depositId}' to claim the {type}refund.");
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"Trying to claim the {type}refund (transaction hash: '{transactionHash}') for deposit: '{depositId}'.");
            var verifierResult = await _transactionVerifier.VerifyAsync(transaction);
            if (!verifierResult.BlockFound)
            {
                if (_logger.IsWarn) _logger.Warn($"Block number: {transaction.BlockNumber}, hash: '{transaction.BlockHash}' was not found for transaction hash: '{transactionHash}' - {type}refund claim for deposit: '{depositId}' will not confirmed.");
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"The {type}refund claim (transaction hash: '{transactionHash}') for deposit: '{depositId}' has {verifierResult.Confirmations} confirmations (required at least {verifierResult.RequiredConfirmations}).");
            if (!verifierResult.Confirmed)
            {
                return;
            }
            
            deposit.SetRefundClaimed();
            await _depositRepository.UpdateAsync(deposit);
            if (_logger.IsInfo) _logger.Info($"The {type}refund claim (transaction hash: '{transactionHash}') for deposit: '{depositId}' has been confirmed.");
        }
    }
}