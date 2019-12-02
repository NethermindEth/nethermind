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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Shared.Services
{
    public class ConsumerTransactionsService : IConsumerTransactionsService
    {
        private readonly ITransactionService _transactionService;
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        public ConsumerTransactionsService(ITransactionService transactionService,
            IDepositDetailsRepository depositRepository, ITimestamper timestamper, ILogManager logManager)
        {
            _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
            _depositRepository = depositRepository ?? throw new ArgumentNullException(nameof(depositRepository));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<IEnumerable<PendingTransaction>> GetPendingAsync()
        {
            var deposits = await _depositRepository.BrowseAsync(new GetDeposits
            {
                OnlyPending = true,
                Page = 1,
                Results = int.MaxValue
            });

            return deposits.Items.Select(MapPendingTransaction);
        }

        private static PendingTransaction MapPendingTransaction(DepositDetails deposit)
            => deposit.ClaimedRefundTransaction is null
                ? new PendingTransaction(deposit.Id.ToString(), "deposit", deposit.Transaction)
                : new PendingTransaction(deposit.Id.ToString(), "refund", deposit.ClaimedRefundTransaction);

        public async Task<Keccak> UpdateDepositGasPriceAsync(Keccak depositId, UInt256 gasPrice)
        {
            if (gasPrice == 0)
            {
                throw new ArgumentException("Gas price cannot be 0.", nameof(gasPrice));
            }
            
            var deposit = await GetDepositAsync(depositId);
            if (deposit.Confirmed)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' was confirmed.");
            }

            var currentHash = deposit.Transaction.Hash;
            var gasLimit = deposit.Transaction.GasLimit;
            if (_logger.IsInfo) _logger.Info($"Updating gas price for deposit with id: '{depositId}', current transaction hash: '{currentHash}'.");
            var transactionHash = await _transactionService.UpdateGasPriceAsync(currentHash, gasPrice);
            if (_logger.IsInfo) _logger.Info($"Received transaction hash: '{transactionHash}' for deposit with id: '{depositId}' after updating gas price.");
            deposit.SetTransaction(new TransactionInfo(transactionHash, deposit.Deposit.Value, gasPrice, gasLimit, _timestamper.EpochSeconds));
            await _depositRepository.UpdateAsync(deposit);
            if (_logger.IsInfo) _logger.Info($"Updated gas price for deposit with id: '{depositId}', transaction hash: '{transactionHash}'.");

            return transactionHash;
        }

        public async Task<Keccak> UpdateRefundGasPriceAsync(Keccak depositId, UInt256 gasPrice)
        {
            if (gasPrice == 0)
            {
                throw new ArgumentException("Gas price cannot be 0.", nameof(gasPrice));
            }
            
            var deposit = await GetDepositAsync(depositId);
            if (deposit.ClaimedRefundTransaction is null)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' has no transaction for refund claim.");
            }

            var currentHash = deposit.ClaimedRefundTransaction.Hash;
            if (deposit.RefundClaimed)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' has already claimed refund (transaction hash: '{currentHash}').");
            }
            
            var gasLimit = deposit.Transaction.GasLimit;
            if (_logger.IsInfo) _logger.Info($"Updating gas price for refund claim for deposit with id: '{depositId}', current transaction hash: '{currentHash}'.");
            var transactionHash = await _transactionService.UpdateGasPriceAsync(currentHash, gasPrice);
            if (_logger.IsInfo) _logger.Info($"Received transaction hash: '{transactionHash}' for deposit with id: '{depositId}' after updating gas price for refund claim.");
            deposit.SetClaimedRefundTransaction(new TransactionInfo(transactionHash, 0, gasPrice, gasLimit, _timestamper.EpochSeconds));
            await _depositRepository.UpdateAsync(deposit);
            if (_logger.IsInfo) _logger.Info($"Updated gas price for refund claim for deposit with id: '{depositId}', transaction hash: '{transactionHash}'.");

            return transactionHash;
        }

        public async Task<Keccak> CancelDepositAsync(Keccak depositId)
        {
            if (_logger.IsWarn) _logger.Warn($"Canceling transaction for deposit with id: '{depositId}'.");
            var deposit = await GetDepositAsync(depositId);
            if (deposit.Confirmed)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' was confirmed.");
            }
            
            if (deposit.Transaction.State != TransactionState.Pending)
            {
                throw new InvalidOperationException($"Cannot cancel transaction with hash: '{deposit.Transaction.Hash}' for deposit with id: '{depositId}' (state: '{deposit.Transaction.State}').");
            }
            
            var transactionHash = await _transactionService.CancelAsync(deposit.Transaction.Hash);
            if (_logger.IsWarn) _logger.Warn($"Canceled transaction for deposit with id: '{depositId}', transaction hash: '{transactionHash}'.");
            
            deposit.Transaction.SetCanceled(transactionHash);
            await _depositRepository.UpdateAsync(deposit);

            return transactionHash;
        }

        public async Task<Keccak> CancelRefundAsync(Keccak depositId)
        {
            if (_logger.IsWarn) _logger.Warn($"Canceling transaction for refund for deposit with id: '{depositId}'.");
            var deposit = await GetDepositAsync(depositId);
            if (deposit.ClaimedRefundTransaction is null)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' has no transaction for refund claim.");
            }
            
            var currentHash = deposit.ClaimedRefundTransaction.Hash;
            if (deposit.RefundClaimed)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' has already claimed refund (transaction hash: '{currentHash}').");
            }
            
            if (deposit.ClaimedRefundTransaction.State != TransactionState.Pending)
            {
                throw new InvalidOperationException($"Cannot cancel transaction with hash: '{deposit.ClaimedRefundTransaction.Hash}' for refund for deposit with id: '{depositId}' (state: '{deposit.ClaimedRefundTransaction.State}').");
            }
            
            var transactionHash = await _transactionService.CancelAsync(currentHash);
            if (_logger.IsWarn) _logger.Warn($"Canceled transaction for deposit with id: '{depositId}', transaction hash: '{transactionHash}'.");
            
            deposit.ClaimedRefundTransaction.SetCanceled(transactionHash);
            await _depositRepository.UpdateAsync(deposit);

            return transactionHash;
        }

        private async Task<DepositDetails> GetDepositAsync(Keccak depositId)
        {
            var deposit = await _depositRepository.GetAsync(depositId);
            if (deposit is null)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' was not found.");
            }

            if (deposit.Transaction is null)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' has no transaction.");
            }

            if (deposit.Rejected)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' was rejected.");
            }

            return deposit;
        }
    }
}