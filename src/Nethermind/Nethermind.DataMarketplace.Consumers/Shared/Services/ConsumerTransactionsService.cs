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
        private readonly ILogger _logger;

        public ConsumerTransactionsService(ITransactionService transactionService,
            IDepositDetailsRepository depositRepository, ILogManager logManager)
        {
            _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
            _depositRepository = depositRepository ?? throw new ArgumentNullException(nameof(depositRepository));
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
        {
            if (deposit.ClaimedRefundTransactionHash is null)
            {
                return new PendingTransaction(deposit.TransactionHash, deposit.TransactionGasPrice, "deposit");
            }

            return new PendingTransaction(deposit.ClaimedRefundTransactionHash,
                deposit.ClaimedRefundTransactionGasPrice, "refund");
        }

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
            
            if (_logger.IsInfo) _logger.Info($"Updating gas price for deposit with id: '{depositId}', current transaction hash: '{deposit.TransactionHash}'.");
            var transactionHash = await _transactionService.UpdateGasPriceAsync(deposit.TransactionHash, gasPrice);
            if (_logger.IsInfo) _logger.Info($"Received transaction hash: '{transactionHash}' for deposit with id: '{depositId}' after updating gas price.");
            deposit.SetTransactionHash(transactionHash, gasPrice);
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
            if (deposit.ClaimedRefundTransactionHash is null)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' has no transaction hash for refund claim.");
            }

            if (deposit.RefundClaimed)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' has already claimed refund (transaction hash: '{deposit.ClaimedRefundTransactionHash}').");
            }
            
            if (_logger.IsInfo) _logger.Info($"Updating gas price for refund claim for deposit with id: '{depositId}', current transaction hash: '{deposit.ClaimedRefundTransactionHash}'.");
            var transactionHash = await _transactionService.UpdateGasPriceAsync(deposit.ClaimedRefundTransactionHash, gasPrice);
            if (_logger.IsInfo) _logger.Info($"Received transaction hash: '{transactionHash}' for deposit with id: '{depositId}' after updating gas price for refund claim.");
            deposit.SetClaimedRefundTransactionHash(transactionHash, gasPrice);
            await _depositRepository.UpdateAsync(deposit);
            if (_logger.IsInfo) _logger.Info($"Updated gas price for refund claim for deposit with id: '{depositId}', transaction hash: '{transactionHash}'.");

            return transactionHash;
        }

        public Task<Keccak> CancelAsync(Keccak transactionHash)
        {
            if (transactionHash is null)
            {
                throw new ArgumentException("Transaction hash cannot be empty.", nameof(transactionHash));
            }
            
            return _transactionService.CancelAsync(transactionHash);
        }

        private async Task<DepositDetails> GetDepositAsync(Keccak depositId)
        {
            var deposit = await _depositRepository.GetAsync(depositId);
            if (deposit is null)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' was not found.");
            }

            if (deposit.TransactionHash is null)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' has no transaction hash.");
            }

            if (deposit.Rejected)
            {
                throw new InvalidOperationException($"Deposit with id: '{depositId}' was rejected.");
            }

            return deposit;
        }
    }
}