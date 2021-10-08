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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;
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

        public async Task<IEnumerable<ResourceTransaction>> GetAllTransactionsAsync()
        {
            var deposits = await _depositRepository.BrowseAsync(new GetDeposits
            {
                Page = 1,
                Results = int.MaxValue
            });

            return deposits.Items.SelectMany(MapPendingTransactions);
        }

        public async Task<IEnumerable<ResourceTransaction>> GetPendingAsync()
        {
            var deposits = await _depositRepository.BrowseAsync(new GetDeposits
            {
                OnlyPending = true,
                Page = 1,
                Results = int.MaxValue
            });

            return deposits.Items.SelectMany(MapPendingTransactions);
        }

        private static IEnumerable<ResourceTransaction> MapPendingTransactions(DepositDetails deposit)
        {
            string depositId = deposit.Id.ToString();

            return deposit.ClaimedRefundTransaction is null
                ? deposit.Transactions.Select(t => new ResourceTransaction(depositId, "deposit", t))
                : deposit.ClaimedRefundTransactions.Select(t => new ResourceTransaction(depositId, "refund", t));
        }

        public async Task<UpdatedTransactionInfo> UpdateDepositGasPriceAsync(Keccak depositId, UInt256 gasPrice)
        {
            if (gasPrice == 0)
            {
                if (_logger.IsError) _logger.Error($"Gas price cannot be 0.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.InvalidGasPrice);
            }
            
            (UpdatedTransactionStatus status, DepositDetails? deposit) = await TryGetDepositAsync(depositId);
            if (status != UpdatedTransactionStatus.Ok)
            {
                return new UpdatedTransactionInfo(status);
            }

            if (deposit!.Transaction == null)
            {
                throw new InvalidDataException($"Managed to retrieve deposit with id: '{depositId}' with a null Transaction");
            }
            
            if (deposit!.Confirmed)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' was confirmed, transaction hash: '{deposit!.Transaction.Hash}'.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.ResourceConfirmed);
            }

            Keccak? currentHash = deposit!.Transaction.Hash;
            if (currentHash == null)
            {
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction, null);
            }
            
            ulong gasLimit = deposit!.Transaction.GasLimit;
            if (_logger.IsInfo) _logger.Info($"Updating gas price for deposit with id: '{depositId}', current transaction hash: '{currentHash}'.");
            Keccak? transactionHash = await _transactionService.UpdateGasPriceAsync(currentHash, gasPrice);
            if (_logger.IsInfo) _logger.Info($"Received transaction hash: '{transactionHash}' for deposit with id: '{depositId}' after updating gas price.");
            deposit!.AddTransaction(TransactionInfo.SpeedUp(transactionHash, deposit!.Deposit.Value, gasPrice, gasLimit,
                _timestamper.UnixTime.Seconds));
            await _depositRepository.UpdateAsync(deposit!);
            if (_logger.IsInfo) _logger.Info($"Updated gas price for deposit with id: '{depositId}', transaction hash: '{transactionHash}'.");

            return new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, transactionHash);
        }

        public async Task<UpdatedTransactionInfo> UpdateRefundGasPriceAsync(Keccak depositId, UInt256 gasPrice)
        {
            if (gasPrice == 0)
            {
                if (_logger.IsError) _logger.Error($"Gas price cannot be 0.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.InvalidGasPrice);
            }
            
            (UpdatedTransactionStatus status, DepositDetails? deposit) = await TryGetDepositAsync(depositId);
            if (status != UpdatedTransactionStatus.Ok)
            {
                return new UpdatedTransactionInfo(status);
            }

            if (deposit!.Transaction == null)
            {
                throw new InvalidDataException($"Managed to retrieve deposit {depositId} without Transaction set");
            }
            
            if (deposit!.ClaimedRefundTransaction is null)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' has no transaction for refund claim.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction);
            }
            
            Keccak? currentHash = deposit!.ClaimedRefundTransaction.Hash;
            
            if (deposit!.RefundClaimed)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' has already claimed refund (transaction hash: '{currentHash}').");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.ResourceConfirmed);
            }
            
            if (currentHash == null)
            {
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction, null);
            }
            
            ulong gasLimit = deposit!.Transaction.GasLimit;
            if (_logger.IsInfo) _logger.Info($"Updating gas price for refund claim for deposit with id: '{depositId}', current transaction hash: '{currentHash}'.");
            Keccak transactionHash = await _transactionService.UpdateGasPriceAsync(currentHash, gasPrice);
            if (_logger.IsInfo) _logger.Info($"Received transaction hash: '{transactionHash}' for deposit with id: '{depositId}' after updating gas price for refund claim.");
            deposit.AddClaimedRefundTransaction(TransactionInfo.SpeedUp(transactionHash, 0, gasPrice, gasLimit,
                _timestamper.UnixTime.Seconds));
            await _depositRepository.UpdateAsync(deposit);
            if (_logger.IsInfo) _logger.Info($"Updated gas price for refund claim for deposit with id: '{depositId}', transaction hash: '{transactionHash}'.");

            return new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, transactionHash);
        }

        public async Task<UpdatedTransactionInfo> CancelDepositAsync(Keccak depositId)
        {
            if (_logger.IsWarn) _logger.Warn($"Canceling transaction for deposit with id: '{depositId}'.");
            (UpdatedTransactionStatus status, DepositDetails? deposit) = await TryGetDepositAsync(depositId);
            if (status != UpdatedTransactionStatus.Ok)
            {
                return new UpdatedTransactionInfo(status);
            }

            if (deposit!.Transaction == null)
            {
                throw new InvalidDataException($"Maneged to retrieve deposit with id: '{depositId}' which has no Transaction set.");
            }
            
            if (deposit!.Confirmed)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' was confirmed, transaction hash: '{deposit!.Transaction.Hash}'.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.ResourceConfirmed);
            }

            if (deposit!.Transaction.State != TransactionState.Pending)
            {
                if (_logger.IsError) _logger.Error($"Cannot cancel transaction with hash: '{deposit!.Transaction.Hash}' for deposit with id: '{depositId}' (state: '{deposit.Transaction.State}').");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.AlreadyIncluded);
            }
            
            if (deposit!.Transaction.Hash == null)
            {
                if (_logger.IsError) _logger.Error($"Cannot cancel transaction with hash: '{deposit!.Transaction.Hash}' for deposit with id: '{depositId}' (state: '{deposit.Transaction.State}').");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction);
            }
            
            CanceledTransactionInfo transaction = await _transactionService.CancelAsync(deposit!.Transaction.Hash);
            if (_logger.IsWarn) _logger.Warn($"Canceled transaction for deposit with id: '{depositId}', transaction hash: '{transaction.Hash}'.");
            TransactionInfo cancellingTransaction = TransactionInfo.Cancellation(transaction.Hash, transaction.GasPrice,
                transaction.GasLimit, _timestamper.UnixTime.Seconds);
            deposit!.AddTransaction(cancellingTransaction);
            await _depositRepository.UpdateAsync(deposit);

            return new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, transaction.Hash);
        }

        public async Task<UpdatedTransactionInfo> CancelRefundAsync(Keccak depositId)
        {
            if (_logger.IsWarn) _logger.Warn($"Canceling transaction for refund for deposit with id: '{depositId}'.");
            (UpdatedTransactionStatus status, DepositDetails? deposit) = await TryGetDepositAsync(depositId);
            if (status != UpdatedTransactionStatus.Ok)
            {
                return new UpdatedTransactionInfo(status);
            }
            
            if (deposit!.ClaimedRefundTransaction is null)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' has no transaction for refund claim.");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction);
            }
            
            Keccak? currentHash = deposit!.ClaimedRefundTransaction.Hash;
            if (deposit!.RefundClaimed)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' has already claimed refund (transaction hash: '{currentHash}').");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.ResourceConfirmed);
            }
            
            if (currentHash is null)
            {
                if (_logger.IsError) _logger.Error($"Cannot cancel transaction with hash: '{null}' for refund for deposit with id: '{depositId}' (state: '{deposit.ClaimedRefundTransaction.State}').");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.MissingTransaction);
            }
            
            if (deposit!.ClaimedRefundTransaction.State != TransactionState.Pending)
            {
                if (_logger.IsError) _logger.Error($"Cannot cancel transaction with hash: '{deposit!.ClaimedRefundTransaction.Hash}' for refund for deposit with id: '{depositId}' (state: '{deposit.ClaimedRefundTransaction.State}').");
                return new UpdatedTransactionInfo(UpdatedTransactionStatus.AlreadyIncluded);
            }
            
            CanceledTransactionInfo transaction = await _transactionService.CancelAsync(currentHash);
            if (_logger.IsWarn) _logger.Warn($"Canceled transaction for deposit with id: '{depositId}', transaction hash: '{transaction.Hash}'.");
            TransactionInfo cancellingTransaction = TransactionInfo.Cancellation(transaction.Hash, transaction.GasPrice,
                transaction.GasLimit, _timestamper.UnixTime.Seconds);
            deposit.AddClaimedRefundTransaction(cancellingTransaction);
            await _depositRepository.UpdateAsync(deposit);

            return new UpdatedTransactionInfo(UpdatedTransactionStatus.Ok, transaction.Hash);
        }

        private async Task<(UpdatedTransactionStatus status, DepositDetails? deposit)> TryGetDepositAsync(Keccak depositId)
        {
            DepositDetails? deposit = await _depositRepository.GetAsync(depositId);
            if (deposit is null)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' was not found.");
                return (UpdatedTransactionStatus.ResourceNotFound, null);
            }

            if (deposit.Transaction is null)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' has no transaction.");
                return (UpdatedTransactionStatus.MissingTransaction, null);
            }
            
            if (deposit.Cancelled)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' was cancelled.");
                return (UpdatedTransactionStatus.ResourceCancelled, null);
            }

            if (deposit.Rejected)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' was rejected.");
                return (UpdatedTransactionStatus.ResourceRejected, null);
            }

            return (UpdatedTransactionStatus.Ok, deposit);
        }
    }
}
