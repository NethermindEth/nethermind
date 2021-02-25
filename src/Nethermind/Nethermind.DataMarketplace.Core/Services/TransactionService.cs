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
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class TransactionService : ITransactionService
    {
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly IWallet _wallet;
        private readonly IConfigManager _configManager;
        private readonly string _configId;
        private readonly ILogger _logger;

        public TransactionService(INdmBlockchainBridge blockchainBridge, IWallet wallet, IConfigManager configManager,
            string configId, ILogManager logManager)
        {
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _configId = configId ?? throw new ArgumentNullException(nameof(configId));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<Keccak> UpdateGasPriceAsync(Keccak transactionHash, UInt256 gasPrice)
        {
            if (gasPrice == 0)
            {
                throw new ArgumentException("Gas price cannot be 0.", nameof(gasPrice));
            }

            return await UpdateAsync(transactionHash, transaction =>
            {
                var previousGasPrice = transaction.GasPrice;
                transaction.GasPrice = gasPrice;
                if (_logger.IsInfo) _logger.Info($"Updating transaction with hash: '{transactionHash}' gas price: {previousGasPrice} wei -> {gasPrice} wei.");
            });
        }

        public Task<Keccak> UpdateValueAsync(Keccak transactionHash, UInt256 value) 
            => UpdateAsync(transactionHash, transaction =>
            {
                var previousValue = transaction.Value;
                transaction.Value = value;
                if (_logger.IsInfo) _logger.Info($"Updating transaction with hash: '{transactionHash}' value: {previousValue} wei -> {value} wei.");
            });

        public async Task<CanceledTransactionInfo> CancelAsync(Keccak transactionHash)
        {
            NdmConfig? config = await _configManager.GetAsync(_configId);
            uint multiplier = config?.CancelTransactionGasPricePercentageMultiplier ?? 0;
            if (multiplier == 0)
            {
                throw new InvalidOperationException("Multiplier for gas price when canceling transaction cannot be 0.");
            }

            const long gasLimit = Transaction.BaseTxGasCost;
            UInt256 gasPrice = 0; 
            
            var hash = await UpdateAsync(transactionHash, transaction =>
            {
                gasPrice = multiplier * transaction.GasPrice / 100;
                transaction.GasPrice = gasPrice;
                transaction.GasLimit = gasLimit;
                transaction.Data = null;
                transaction.Value = 0;
                if (_logger.IsInfo) _logger.Info($"Canceling transaction with hash: '{transactionHash}', gas price: {gasPrice} wei ({multiplier}% of original transaction).");
            });

            return new CanceledTransactionInfo(hash, gasPrice, gasLimit);
        }

        private async Task<Keccak> UpdateAsync(Keccak transactionHash, Action<Transaction> update)
        {
            if (transactionHash is null)
            {
                throw new ArgumentException("Transaction hash cannot be null.", nameof(transactionHash));
            }
            
            var transactionDetails = await _blockchainBridge.GetTransactionAsync(transactionHash);
            if (transactionDetails is null)
            {
                throw new ArgumentException($"Transaction was not found for hash: '{transactionHash}'.", nameof(transactionHash));
            }

            if (!transactionDetails.IsPending)
            {
                throw new InvalidOperationException($"Transaction with hash: '{transactionHash}' is not pending.");
            }
            
            var transaction = transactionDetails.Transaction;
            update(transaction);
            _wallet.Sign(transaction, await _blockchainBridge.GetNetworkIdAsync());
            var hash = await _blockchainBridge.SendOwnTransactionAsync(transaction);
            if (hash is null)
            {
                throw new InvalidOperationException("Transaction was not sent (received an empty hash).");
            }

            if (_logger.IsInfo) _logger.Info($"Received a new transaction hash: '{hash}' (previous transaction hash: '{transactionHash}').");

            return hash;
        }
    }
}
