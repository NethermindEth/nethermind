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
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.Services
{
    public class RefundService : IRefundService
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IAbiEncoder _abiEncoder;
        private readonly IWallet _wallet;
        private readonly Address _contractAddress;
        private readonly ILogger _logger;

        public RefundService(IBlockchainBridge blockchainBridge, IAbiEncoder abiEncoder, IWallet wallet,
            Address contractAddress, ILogManager logManager)
        {
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _contractAddress = contractAddress ?? throw new ArgumentNullException(nameof(contractAddress));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public Keccak ClaimRefund(Address onBehalfOf, RefundClaim refundClaim)
        {
            byte[] txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractData.ClaimRefundSig, refundClaim.AssetId.Bytes, refundClaim.Units, refundClaim.Value, refundClaim.ExpiryTime, refundClaim.Pepper, refundClaim.Provider, onBehalfOf);
            Transaction transaction = new Transaction();
            transaction.Value = 0;
            transaction.Data = txData;
            transaction.To = _contractAddress;
            transaction.SenderAddress = onBehalfOf;
            transaction.GasLimit = 90000; // check  
            transaction.GasPrice = 20.GWei();
            transaction.Nonce = (UInt256) _blockchainBridge.GetNonce(onBehalfOf);
            _wallet.Sign(transaction, _blockchainBridge.GetNetworkId());
            
            if (_logger.IsInfo)
            {
                _logger.Info($"Sending a refund claim transaction for {refundClaim.DepositId} to be refunded to {refundClaim.RefundTo}");
            }
            
            return _blockchainBridge.SendTransaction(transaction, true);
        }

        public Keccak ClaimEarlyRefund(Address onBehalfOf, EarlyRefundClaim earlyRefundClaim)
        {
            byte[] txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractData.ClaimEarlyRefundSig, earlyRefundClaim.AssetId.Bytes, earlyRefundClaim.Units, earlyRefundClaim.Value, earlyRefundClaim.ExpiryTime, earlyRefundClaim.Pepper, earlyRefundClaim.Provider, earlyRefundClaim.ClaimableAfter, earlyRefundClaim.Signature.V, earlyRefundClaim.Signature.R, earlyRefundClaim.Signature.S, onBehalfOf);
            Transaction transaction = new Transaction();
            transaction.Value = 0;
            transaction.Data = txData;
            transaction.To = _contractAddress;
            transaction.SenderAddress = onBehalfOf;
            transaction.GasLimit = 90000; // check  
            transaction.GasPrice = 20.GWei();
            transaction.Nonce = (UInt256) _blockchainBridge.GetNonce(onBehalfOf);
            
            if (_logger.IsInfo)
            {
                _logger.Info($"Sending an early refund claim transaction on {earlyRefundClaim.DepositId} to be refunded to {earlyRefundClaim.RefundTo}");
            }
            
            _wallet.Sign(transaction, _blockchainBridge.GetNetworkId());
            return _blockchainBridge.SendTransaction(transaction, true);
        }
    }
}