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

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Shared.Services.Models;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Refunds.Services
{
    public class RefundClaimant : IRefundClaimant
    {
        private readonly IRefundService _refundService;
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly ITransactionVerifier _transactionVerifier;
        private readonly IGasPriceService _gasPriceService;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        public RefundClaimant(IRefundService refundService, INdmBlockchainBridge blockchainBridge,
            IDepositDetailsRepository depositRepository, ITransactionVerifier transactionVerifier,
            IGasPriceService gasPriceService, ITimestamper timestamper, ILogManager logManager)
        {
            _refundService = refundService;
            _blockchainBridge = blockchainBridge;
            _depositRepository = depositRepository;
            _transactionVerifier = transactionVerifier;
            _gasPriceService = gasPriceService;
            _timestamper = timestamper;
            _logger = logManager.GetClassLogger();
        }

        public async Task<RefundClaimStatus> TryClaimRefundAsync(DepositDetails deposit, Address refundTo)
        {
            ulong now = _timestamper.UnixTime.Seconds;
            if (!deposit.CanClaimRefund(now))
            {
                DisplayRefundInfo(deposit, now);
                return RefundClaimStatus.Empty;
            }
            
            Block? latestBlock = await _blockchainBridge.GetLatestBlockAsync();
            if (latestBlock == null)
            {
                return RefundClaimStatus.Empty;
            }
            
            now = (ulong) latestBlock.Timestamp;
            if (!deposit.CanClaimRefund(now))
            {
                DisplayRefundInfo(deposit, now);
                return RefundClaimStatus.Empty;
            }

            Keccak depositId = deposit.Deposit.Id;
            Keccak? transactionHash = deposit.ClaimedRefundTransaction?.Hash;
            if (transactionHash is null)
            {
                Address provider = deposit.DataAsset.Provider.Address;
                RefundClaim refundClaim = new RefundClaim(depositId, deposit.DataAsset.Id, deposit.Deposit.Units,
                    deposit.Deposit.Value, deposit.Deposit.ExpiryTime, deposit.Pepper, provider, refundTo);
                UInt256 gasPrice = await _gasPriceService.GetCurrentRefundGasPriceAsync();
                transactionHash = await _refundService.ClaimRefundAsync(refundTo, refundClaim, gasPrice);
                if (transactionHash is null)
                {
                    if (_logger.IsError) _logger.Error("There was an error when trying to claim refund (no transaction hash returned).");
                    return RefundClaimStatus.Empty;
                }

                deposit.AddClaimedRefundTransaction(TransactionInfo.Default(transactionHash, 0, gasPrice,
                    _refundService.GasLimit, _timestamper.UnixTime.Seconds));
                await _depositRepository.UpdateAsync(deposit);
                if (_logger.IsInfo) _logger.Info($"Claimed a refund for deposit: '{depositId}', gas price: {gasPrice} wei, transaction hash: '{transactionHash}' (awaits a confirmation).");
            }

            bool confirmed = await TryConfirmClaimAsync(deposit, string.Empty);

            return confirmed
                ? RefundClaimStatus.Confirmed(transactionHash)
                : RefundClaimStatus.Unconfirmed(transactionHash);
        }

        private void DisplayRefundInfo(DepositDetails deposit, ulong now)
        {
            var timeLeftToClaimRefund = deposit.GetTimeLeftToClaimRefund(now);
            if (timeLeftToClaimRefund > 0)
            {
                if (_logger.IsInfo) _logger.Info($"Time left to claim a refund: {timeLeftToClaimRefund} seconds.");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info("Deposit is not claimable.");
            }
        }

        public async Task<RefundClaimStatus> TryClaimEarlyRefundAsync(DepositDetails deposit, Address refundTo)
        {
            ulong now = _timestamper.UnixTime.Seconds;
            if (!deposit.CanClaimEarlyRefund(now, deposit.Timestamp))
            {
                return RefundClaimStatus.Empty;
            }
            
            Block? latestBlock = await _blockchainBridge.GetLatestBlockAsync();
            if (latestBlock == null)
            {
                return RefundClaimStatus.Empty;
            }
            
            now = (ulong) latestBlock.Timestamp;
            if (!deposit.CanClaimEarlyRefund(now, deposit.Timestamp))
            {
                return RefundClaimStatus.Empty;
            }
            
            Keccak depositId = deposit.Deposit.Id;
            Keccak? transactionHash = deposit.ClaimedRefundTransaction?.Hash;
            if (transactionHash is null)
            {
                Address provider = deposit.DataAsset.Provider.Address;
                if (deposit.EarlyRefundTicket == null)
                {
                    throw new InvalidDataException($"Early refund ticket is null on a claimable deposit {depositId}");
                }
                
                EarlyRefundTicket ticket = deposit.EarlyRefundTicket;
                EarlyRefundClaim earlyRefundClaim = new EarlyRefundClaim(ticket.DepositId, deposit.DataAsset.Id,
                    deposit.Deposit.Units, deposit.Deposit.Value, deposit.Deposit.ExpiryTime, deposit.Pepper, provider,
                    ticket.ClaimableAfter, ticket.Signature, refundTo);
                UInt256 gasPrice = await _gasPriceService.GetCurrentRefundGasPriceAsync();
                transactionHash = await _refundService.ClaimEarlyRefundAsync(refundTo, earlyRefundClaim, gasPrice);
                if (transactionHash is null)
                {
                    if (_logger.IsError) _logger.Error("There was an error when trying to claim early refund (no transaction hash returned).");
                    return RefundClaimStatus.Empty;
                }

                deposit.AddClaimedRefundTransaction(TransactionInfo.Default(transactionHash, 0, gasPrice,
                    _refundService.GasLimit, _timestamper.UnixTime.Seconds));
                await _depositRepository.UpdateAsync(deposit);
                if (_logger.IsInfo) _logger.Info($"Claimed an early refund for deposit: '{depositId}', gas price: {gasPrice} wei, transaction hash: '{transactionHash}' (awaits a confirmation).");
            }

            bool confirmed = await TryConfirmClaimAsync(deposit, "early ");
            
            return confirmed
                ? RefundClaimStatus.Confirmed(transactionHash)
                : RefundClaimStatus.Unconfirmed(transactionHash);
        }

        private async Task<bool> TryConfirmClaimAsync(DepositDetails deposit, string type)
        {
            string claimType = $"{type}refund";
            Keccak depositId = deposit.Id;
            
            NdmTransaction? transactionDetails = null;
            TransactionInfo includedTransaction = deposit.Transactions.SingleOrDefault(t => t.State == TransactionState.Included);
            IOrderedEnumerable<TransactionInfo> pendingTransactions = deposit.Transactions
                .Where(t => t.State == TransactionState.Pending)
                .OrderBy(t => t.Timestamp);

            if (_logger.IsInfo) _logger.Info($"Deposit: '{deposit.Id}' refund claim pending transactions: {string.Join(", ", pendingTransactions.Select(t => $"{t.Hash} [{t.Type}]"))}");
            
            if (includedTransaction is null)
            {
                foreach (TransactionInfo transaction in pendingTransactions)
                {
                    Keccak? transactionHash = transaction.Hash;
                    if (transactionHash is null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Transaction was not found for hash: '{null}' for deposit: '{depositId}' to claim the {claimType}.");
                        return false;
                    }
                    
                    transactionDetails  = await _blockchainBridge.GetTransactionAsync(transactionHash);
                    if (transactionDetails is null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Transaction was not found for hash: '{transactionHash}' for deposit: '{depositId}' to claim the {claimType}.");
                        return false;
                    }
            
                    if (transactionDetails.IsPending)
                    {
                        if (_logger.IsInfo) _logger.Info($"Transaction with hash: '{transactionHash}' for deposit: '{deposit.Id}' {claimType} claim is still pending.");
                        return false;
                    }

                    deposit.SetIncludedClaimedRefundTransaction(transactionHash);
                    if (_logger.IsInfo) _logger.Info($"Transaction with hash: '{transactionHash}', type: '{transaction.Type}' for deposit: '{deposit.Id}' {claimType} claim was included into block: {transactionDetails.BlockNumber}.");
                    await _depositRepository.UpdateAsync(deposit);
                    includedTransaction = transaction;
                    break;
                }
            }
            else if (includedTransaction.Type == TransactionType.Cancellation)
            {
                return false;
            }
            else
            {
                transactionDetails = includedTransaction.Hash == null ? null : await _blockchainBridge.GetTransactionAsync(includedTransaction.Hash);
                if (transactionDetails is null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Transaction (set as included) was not found for hash: '{includedTransaction.Hash}' for deposit: '{deposit.Id}' {claimType} claim.");
                    return false;
                }
            }
            
            if (includedTransaction is null)
            {
                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Trying to claim the {claimType} (transaction hash: '{includedTransaction.Hash}') for deposit: '{depositId}'.");
            TransactionVerifierResult verifierResult = await _transactionVerifier.VerifyAsync(transactionDetails!);
            if (!verifierResult.BlockFound)
            {
                if (_logger.IsWarn) _logger.Warn($"Block number: {transactionDetails!.BlockNumber}, hash: '{transactionDetails.BlockHash}' was not found for transaction hash: '{includedTransaction.Hash}' - {claimType} claim for deposit: '{depositId}' will not confirmed.");
                return false;
            }
            
            if (_logger.IsInfo) _logger.Info($"The {claimType} claim (transaction hash: '{includedTransaction.Hash}') for deposit: '{depositId}' has {verifierResult.Confirmations} confirmations (required at least {verifierResult.RequiredConfirmations}).");
            if (!verifierResult.Confirmed)
            {
                return false;
            }
            
            deposit.SetRefundClaimed();
            await _depositRepository.UpdateAsync(deposit);
            if (_logger.IsInfo) _logger.Info($"The {claimType} claim (transaction hash: '{includedTransaction.Hash}') for deposit: '{depositId}' has been confirmed.");

            return true;
        }
    }
}
