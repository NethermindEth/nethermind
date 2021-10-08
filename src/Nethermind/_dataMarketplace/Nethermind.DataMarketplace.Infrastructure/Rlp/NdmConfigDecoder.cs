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
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class NdmConfigDecoder : IRlpNdmDecoder<NdmConfig>
    {
        public static void Init()
        {
        }
        
        static NdmConfigDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(NdmConfig)] = new NdmConfigDecoder();
        }

        public NdmConfig Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                bool enabled = rlpStream.DecodeBool();
                string id = rlpStream.DecodeString();
                string initializerName = rlpStream.DecodeString();
                bool storeConfigInDatabase = rlpStream.DecodeBool();
                bool verifyP2PSignature = rlpStream.DecodeBool();
                string persistence = rlpStream.DecodeString();
                bool faucetEnabled = rlpStream.DecodeBool();
                string faucetAddress = rlpStream.DecodeString();
                string faucetHost = rlpStream.DecodeString();
                UInt256 faucetWeiRequestMaxValue = rlpStream.DecodeUInt256();
                UInt256 faucetEthDailyRequestsTotalValue = rlpStream.DecodeUInt256();
                string consumerAddress = rlpStream.DecodeString();
                string contractAddress = rlpStream.DecodeString();
                string providerName = rlpStream.DecodeString();
                string providerAddress = rlpStream.DecodeString();
                string providerColdWalletAddress = rlpStream.DecodeString();
                UInt256 receiptRequestThreshold = rlpStream.DecodeUInt256();
                UInt256 receiptsMergeThreshold = rlpStream.DecodeUInt256();
                UInt256 paymentClaimThreshold = rlpStream.DecodeUInt256();
                uint blockConfirmations = rlpStream.DecodeUInt();
                string filesPath = rlpStream.DecodeString();
                ulong fileMaxSize = rlpStream.DecodeUlong();
                string pluginsPath = rlpStream.DecodeString();
                string databasePath = rlpStream.DecodeString();
                bool proxyEnabled = rlpStream.DecodeBool();
                var jsonRpcUrlProxies = rlpStream.DecodeArray(c => c.DecodeString());
                string gasPriceType = rlpStream.DecodeString();
                UInt256 gasPrice = rlpStream.DecodeUInt256();
                uint cancelTransactionGasPricePercentageMultiplier = rlpStream.DecodeUInt();
                bool jsonRpcDataChannelEnabled = rlpStream.DecodeBool();
                UInt256 refundGasPrice = rlpStream.DecodeUInt256();
                UInt256 paymentClaimGasPrice = rlpStream.DecodeUInt256();

                return new NdmConfig
                {
                    Enabled = enabled,
                    Id = id,
                    InitializerName = initializerName,
                    StoreConfigInDatabase = storeConfigInDatabase,
                    VerifyP2PSignature = verifyP2PSignature,
                    Persistence = persistence,
                    FaucetEnabled = faucetEnabled,
                    FaucetAddress = faucetAddress == string.Empty ? null : faucetAddress,
                    FaucetHost = faucetHost == string.Empty ? null : faucetHost,
                    FaucetWeiRequestMaxValue = faucetWeiRequestMaxValue,
                    FaucetEthDailyRequestsTotalValue = faucetEthDailyRequestsTotalValue,
                    ConsumerAddress = consumerAddress == string.Empty ? null : consumerAddress,
                    ContractAddress = contractAddress == string.Empty ? null : contractAddress,
                    ProviderName = providerName,
                    ProviderAddress = providerAddress == string.Empty ? null : providerAddress,
                    ProviderColdWalletAddress = providerColdWalletAddress == string.Empty ? null : providerColdWalletAddress,
                    ReceiptRequestThreshold = receiptRequestThreshold,
                    ReceiptsMergeThreshold = receiptsMergeThreshold,
                    PaymentClaimThreshold = paymentClaimThreshold,
                    BlockConfirmations = blockConfirmations,
                    FilesPath = filesPath,
                    FileMaxSize = fileMaxSize,
                    PluginsPath = pluginsPath,
                    DatabasePath = databasePath,
                    ProxyEnabled = proxyEnabled,
                    JsonRpcUrlProxies = jsonRpcUrlProxies!,
                    GasPriceType = gasPriceType,
                    GasPrice = gasPrice,
                    CancelTransactionGasPricePercentageMultiplier = cancelTransactionGasPricePercentageMultiplier,
                    JsonRpcDataChannelEnabled = jsonRpcDataChannelEnabled,
                    RefundGasPrice = refundGasPrice,
                    PaymentClaimGasPrice = paymentClaimGasPrice
                };
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(NdmConfig)} cannot be deserialized from", e);
            }
        }

        public void Encode(RlpStream stream, NdmConfig item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(NdmConfig item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Enabled),
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.InitializerName),
                Serialization.Rlp.Rlp.Encode(item.StoreConfigInDatabase),
                Serialization.Rlp.Rlp.Encode(item.VerifyP2PSignature),
                Serialization.Rlp.Rlp.Encode(item.Persistence),
                Serialization.Rlp.Rlp.Encode(item.FaucetEnabled),
                Serialization.Rlp.Rlp.Encode(item.FaucetAddress),
                Serialization.Rlp.Rlp.Encode(item.FaucetHost),
                Serialization.Rlp.Rlp.Encode(item.FaucetWeiRequestMaxValue),
                Serialization.Rlp.Rlp.Encode(item.FaucetEthDailyRequestsTotalValue),
                Serialization.Rlp.Rlp.Encode(item.ConsumerAddress),
                Serialization.Rlp.Rlp.Encode(item.ContractAddress),
                Serialization.Rlp.Rlp.Encode(item.ProviderName),
                Serialization.Rlp.Rlp.Encode(item.ProviderAddress),
                Serialization.Rlp.Rlp.Encode(item.ProviderColdWalletAddress),
                Serialization.Rlp.Rlp.Encode(item.ReceiptRequestThreshold),
                Serialization.Rlp.Rlp.Encode(item.ReceiptsMergeThreshold),
                Serialization.Rlp.Rlp.Encode(item.PaymentClaimThreshold),
                Serialization.Rlp.Rlp.Encode(item.BlockConfirmations),
                Serialization.Rlp.Rlp.Encode(item.FilesPath),
                Serialization.Rlp.Rlp.Encode(item.FileMaxSize),
                Serialization.Rlp.Rlp.Encode(item.PluginsPath),
                Serialization.Rlp.Rlp.Encode(item.DatabasePath),
                Serialization.Rlp.Rlp.Encode(item.ProxyEnabled),
                Serialization.Rlp.Rlp.Encode(item.JsonRpcUrlProxies),
                Serialization.Rlp.Rlp.Encode(item.GasPriceType),
                Serialization.Rlp.Rlp.Encode(item.GasPrice),
                Serialization.Rlp.Rlp.Encode(item.CancelTransactionGasPricePercentageMultiplier),
                Serialization.Rlp.Rlp.Encode(item.JsonRpcDataChannelEnabled),
                Serialization.Rlp.Rlp.Encode(item.RefundGasPrice),
                Serialization.Rlp.Rlp.Encode(item.PaymentClaimGasPrice));
        }

        public int GetLength(NdmConfig item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
