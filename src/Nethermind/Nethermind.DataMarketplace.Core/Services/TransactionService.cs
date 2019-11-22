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
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class TransactionService : ITransactionService
    {
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly IWallet _wallet;
        private readonly ILogger _logger;

        public TransactionService(INdmBlockchainBridge blockchainBridge, IWallet wallet, ILogManager logManager)
        {
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<Keccak> UpdateGasPriceAsync(Keccak transactionHash, UInt256 gasPrice)
        {
            if (gasPrice == 0)
            {
                throw new InvalidOperationException($"Gas price cannot be 0.");
            }
            
            var transactionDetails = await _blockchainBridge.GetTransactionAsync(transactionHash);
            if (transactionDetails is null)
            {
                throw new InvalidOperationException($"Transaction was not found for hash: '{transactionHash}'.");
            }

            var transaction = transactionDetails.Transaction;
            if (_logger.IsInfo) _logger.Info($"Updating transaction with hash: '{transactionHash}' gas price: {transaction.GasPrice} -> {gasPrice} wei.");
            transaction.GasPrice = gasPrice;
            _wallet.Sign(transaction, await _blockchainBridge.GetNetworkIdAsync());
            var hash = await _blockchainBridge.SendOwnTransactionAsync(transaction);
            if (hash is null)
            {
                throw new InvalidOperationException("Transaction was not sent (received an empty hash).");
            }
            
            if (_logger.IsInfo) _logger.Info($"Received a new transaction hash: '{hash}' after updating transaction with hash: '{transactionHash}' gas price.");

            return hash;
        }
    }
}