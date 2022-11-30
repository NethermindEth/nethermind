// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class DepositService : IDepositService
    {
        private readonly IAbiEncoder _abiEncoder;
        private readonly INdmBlockchainBridge _blockchainBridge;
        private readonly IWallet _wallet;
        private readonly Address _contractAddress;

        public DepositService(INdmBlockchainBridge blockchainBridge, IAbiEncoder abiEncoder, IWallet wallet,
           Address contractAddress)
        {
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _contractAddress = contractAddress ?? throw new ArgumentNullException(nameof(contractAddress));
        }

        public ulong GasLimit { get; } = 55000;

        public async Task<UInt256> ReadDepositBalanceAsync(Address onBehalfOf, Keccak depositId)
        {
            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractData.DepositBalanceAbiSig,
                depositId.Bytes);
            Transaction transaction = new Transaction
            {
                Value = 0,
                Data = txData,
                To = _contractAddress,
                SenderAddress = onBehalfOf,
                GasLimit = 100000,
                GasPrice = 0.GWei(),
                Nonce = await _blockchainBridge.GetNonceAsync(onBehalfOf)
            };

            var data = await _blockchainBridge.CallAsync(transaction);

            return data.ToUInt256();
        }

        public async Task ValidateContractAddressAsync(Address contractAddress)
        {
            if (contractAddress != _contractAddress)
            {
                throw new InvalidDataException($"Contract address {contractAddress} is different than configured {_contractAddress}");
            }

            var code = await _blockchainBridge.GetCodeAsync(contractAddress);
            if (code is null || code.Length == 0)
            {
                throw new InvalidDataException($"No contract code at address {contractAddress}.");
            }

            if (!Bytes.AreEqual(code, Bytes.FromHexString(ContractData.DeployedCode)))
            {
                throw new InvalidDataException($"Code at address {contractAddress} is different than expected.");
            }
        }


        public async Task<Keccak?> MakeDepositAsync(Address onBehalfOf, Deposit deposit, UInt256 gasPrice)
        {
            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractData.DepositAbiSig,
                deposit.Id.Bytes, deposit.Units, deposit.ExpiryTime);
            var nonce = await _blockchainBridge.GetNonceAsync(onBehalfOf);
            Transaction transaction = new Transaction
            {
                Value = deposit.Value,
                Data = txData,
                To = _contractAddress,
                SenderAddress = onBehalfOf,
                GasLimit = (long)GasLimit,
                GasPrice = gasPrice,
                Nonce = nonce
            };
            // check  
            _wallet.Sign(transaction, await _blockchainBridge.GetNetworkIdAsync());

            return await _blockchainBridge.SendOwnTransactionAsync(transaction);
        }

        public async Task<uint> VerifyDepositAsync(Address onBehalfOf, Keccak depositId)
        {
            var transaction = await GetTransactionAsync(onBehalfOf, depositId);
            var data = await _blockchainBridge.CallAsync(transaction);

            return data.AsSpan().ReadEthUInt32();
        }

        public async Task<uint> VerifyDepositAsync(Address onBehalfOf, Keccak depositId, long blockNumber)
        {
            var transaction = await GetTransactionAsync(onBehalfOf, depositId);
            var data = await _blockchainBridge.CallAsync(transaction, blockNumber);

            return data.AsSpan().ReadEthUInt32();
        }

        private async Task<Transaction> GetTransactionAsync(Address onBehalfOf, Keccak depositId)
            => new Transaction
            {
                Value = 0,
                Data = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractData.VerifyDepositAbiSig,
                    depositId.Bytes),
                To = _contractAddress,
                SenderAddress = onBehalfOf,
                GasLimit = 100000,
                GasPrice = 0.GWei(),
                Nonce = await _blockchainBridge.GetNonceAsync(onBehalfOf)
            };
    }
}
