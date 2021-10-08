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
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositConfirmationService : IDepositConfirmationService
    {
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IDepositService _depositService;
        private readonly ILogger _logger;
        private readonly uint _requiredBlockConfirmations;

        public DepositConfirmationService(INdmBlockchainBridge blockchainBridge, IConsumerNotifier consumerNotifier,
            IDepositDetailsRepository depositRepository, IDepositService depositService, ILogManager logManager,
            uint requiredBlockConfirmations)
        {
            _blockchainBridge = blockchainBridge;
            _consumerNotifier = consumerNotifier;
            _depositRepository = depositRepository;
            _depositService = depositService;
            _logger = logManager.GetClassLogger();
            _requiredBlockConfirmations = requiredBlockConfirmations;
        }
        
        public async Task TryConfirmAsync(DepositDetails deposit)
        {
            if (deposit.Confirmed || deposit.Rejected || deposit.Cancelled || deposit.Transaction is null)
            {
                return;
            }

            NdmTransaction? transactionDetails = null;
            TransactionInfo includedTransaction = deposit.Transactions.SingleOrDefault(t => t.State == TransactionState.Included);
            IOrderedEnumerable<TransactionInfo> pendingTransactions = deposit.Transactions
                .Where(t => t.State == TransactionState.Pending)
                .OrderBy(t => t.Timestamp);

            if (_logger.IsInfo) _logger.Info($"Deposit: '{deposit.Id}' pending transactions: {string.Join(", ", pendingTransactions.Select(t => $"{t.Hash} [{t.Type}]"))}");
            
            if (includedTransaction is null)
            {
                foreach (TransactionInfo transaction in pendingTransactions)
                {
                    Keccak? transactionHash = transaction.Hash;
                    if (transactionHash is null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Transaction was not found for hash: '{null}' for deposit: '{deposit.Id}' to be confirmed.");
                        continue;
                    }
                    
                    transactionDetails = await _blockchainBridge.GetTransactionAsync(transactionHash);
                    if (transactionDetails is null)
                    {
                        if (_logger.IsInfo) _logger.Info($"Transaction was not found for hash: '{transactionHash}' for deposit: '{deposit.Id}' to be confirmed.");
                        continue;
                    }

                    if (transactionDetails.IsPending)
                    {
                        if (_logger.IsInfo) _logger.Info($"Transaction with hash: '{transactionHash}' for deposit: '{deposit.Id}' is still pending.");
                        continue;
                    }

                    deposit.SetIncludedTransaction(transactionHash);
                    if (_logger.IsInfo) _logger.Info($"Transaction with hash: '{transactionHash}', type: '{transaction.Type}' for deposit: '{deposit.Id}' was included into block: {transactionDetails.BlockNumber}.");
                    await _depositRepository.UpdateAsync(deposit);
                    includedTransaction = transaction;
                    break;
                }
            }
            else if (includedTransaction.Type == TransactionType.Cancellation)
            {
                return;
            }
            else
            {
                transactionDetails = includedTransaction.Hash == null ? null : await _blockchainBridge.GetTransactionAsync(includedTransaction.Hash);
                if (transactionDetails is null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Transaction (set as included) was not found for hash: '{includedTransaction.Hash}' for deposit: '{deposit.Id}'.");
                    return;
                }
            }

            if (includedTransaction is null)
            {
                return;
            }
            
            long headNumber = await _blockchainBridge.GetLatestBlockNumberAsync();
            (uint confirmations, bool rejected) = await VerifyDepositConfirmationsAsync(deposit, transactionDetails!, headNumber);
            if (rejected)
            {
                deposit.Reject();
                await _depositRepository.UpdateAsync(deposit);
                await _consumerNotifier.SendDepositRejectedAsync(deposit.Id);
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Deposit: '{deposit.Id}' has {confirmations} confirmations (required at least {_requiredBlockConfirmations}) for transaction hash: '{includedTransaction.Hash}' to be confirmed.");
            bool confirmed = confirmations >= _requiredBlockConfirmations;
            if (confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit with id: '{deposit.Deposit.Id}' has been confirmed.");
            }

            if (confirmations != deposit.Confirmations || confirmed)
            {
                deposit.SetConfirmations(confirmations);
                await _depositRepository.UpdateAsync(deposit);
            }

            await _consumerNotifier.SendDepositConfirmationsStatusAsync(deposit.Id, deposit.DataAsset.Name,
                confirmations, _requiredBlockConfirmations, deposit.ConfirmationTimestamp, confirmed);
        }
        
        private async Task<(uint confirmations, bool rejected)> VerifyDepositConfirmationsAsync(DepositDetails deposit,
            NdmTransaction transaction, long headNumber)
        {
            if (headNumber <= transaction.BlockNumber)
            {
                return (0, false);
            }
            
            Keccak? transactionHash = deposit.Transaction?.Hash;
            uint confirmations = 0u;
            Block? block = await _blockchainBridge.FindBlockAsync(headNumber);
            do
            {
                if (block is null)
                {
                    if (_logger.IsWarn) _logger.Warn("Block was not found.");
                    return (0, false);
                }

                uint confirmationTimestamp = await _depositService.VerifyDepositAsync(deposit.Consumer, deposit.Id, block.Header.Number);
                if (confirmationTimestamp > 0)
                {
                    confirmations++;
                    if (_logger.IsInfo) _logger.Info($"Deposit: '{deposit.Id}' has been confirmed in block number: {block.Number}, hash: '{block.Hash}', transaction hash: '{transactionHash}', timestamp: {confirmationTimestamp}.");
                    if (deposit.ConfirmationTimestamp == 0)
                    {
                        deposit.SetConfirmationTimestamp(confirmationTimestamp);
                        await _depositRepository.UpdateAsync(deposit);
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Deposit with id: '{deposit.Id}' has not returned confirmation timestamp from the contract call yet.'");
                    return (0, false);
                }
                
                if (confirmations == _requiredBlockConfirmations)
                {
                    break;
                }

                if (transaction.BlockHash == block.Hash || block.Number <= transaction.BlockNumber)
                {
                    break;
                }
                
                if (block.ParentHash is null)
                {
                    break;
                }

                block = await _blockchainBridge.FindBlockAsync(block.ParentHash);
            } while (confirmations < _requiredBlockConfirmations);
            
            long latestBlockNumber = await _blockchainBridge.GetLatestBlockNumberAsync();
            long? blocksDifference = latestBlockNumber - transaction.BlockNumber;
            if (blocksDifference >= _requiredBlockConfirmations && confirmations < _requiredBlockConfirmations)
            {
                if (_logger.IsError) _logger.Error($"Deposit: '{deposit.Id}' has been rejected - missing confirmation in block number: {block!.Number}, hash: {block!.Hash}' (transaction hash: '{transactionHash}').");
                return (confirmations, true);
            }

            return (confirmations, false);
        }
    }
}
