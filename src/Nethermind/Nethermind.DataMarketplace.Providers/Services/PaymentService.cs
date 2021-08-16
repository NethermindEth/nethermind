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
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Wallet;
using Nethermind.TxPool;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public class PaymentService : IPaymentService
    {
        private readonly INdmBlockchainBridge _bridge;
        private readonly IAbiEncoder _abiEncoder;
        private readonly IWallet _wallet;
        private readonly Address _contractAddress;
        private readonly ILogger _logger;
        private readonly ITxPool _txPool;

        public PaymentService(INdmBlockchainBridge bridge, IAbiEncoder abiEncoder, IWallet wallet,
            Address contractAddress, ILogManager logManager, ITxPool txPool)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(abiEncoder));
            _contractAddress = contractAddress ?? throw new ArgumentNullException(nameof(contractAddress));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public ulong GasLimit { get; } = 120000;

        public async Task<Keccak?> ClaimPaymentAsync(PaymentClaim paymentClaim, Address coldWalletAddress,
            UInt256 gasPrice)
        {
            byte[] txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature,
                ContractData.ClaimPaymentAbiSig,
                paymentClaim.AssetId.Bytes,
                paymentClaim.Units,
                paymentClaim.Value,
                paymentClaim.ExpiryTime,
                paymentClaim.Pepper,
                coldWalletAddress,
                paymentClaim.Consumer,
                new[] {paymentClaim.UnitsRange.From, paymentClaim.UnitsRange.To},
                paymentClaim.Signature.V,
                paymentClaim.Signature.R,
                paymentClaim.Signature.S);

            if (_logger.IsInfo)
            {
                _logger.Info($"Sending a payment claim transaction - Range: [{paymentClaim.UnitsRange.From},{paymentClaim.UnitsRange.To}] Units: {paymentClaim.Units} to be paid out to {coldWalletAddress}");
            }
            
            Transaction transaction = new Transaction
            {
                Value = 0,
                Data = txData,
                To = _contractAddress,
                SenderAddress = paymentClaim.Provider,
                GasLimit = (long) GasLimit, // when account does not exist then we pay for account creation of cold wallet
                GasPrice = gasPrice,
                Nonce = await _bridge.GetNonceAsync(paymentClaim.Provider)
            };

            // check  
            _wallet.Sign(transaction, await _bridge.GetNetworkIdAsync());
            
            return await _bridge.SendOwnTransactionAsync(transaction);
        }
    }
}
