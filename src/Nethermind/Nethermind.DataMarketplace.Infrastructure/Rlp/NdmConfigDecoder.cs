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

using System.IO;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class NdmConfigDecoder : IRlpDecoder<NdmConfig>
    {
        public NdmConfigDecoder()
        {
        }

        static NdmConfigDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(NdmConfig)] = new NdmConfigDecoder();
        }

        public NdmConfig Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var enabled = rlpStream.DecodeBool();
            var id = rlpStream.DecodeString();
            var initializerName = rlpStream.DecodeString();
            var storeConfigInDatabase = rlpStream.DecodeBool();
            var verifyP2PSignature = rlpStream.DecodeBool();
            var persistence = rlpStream.DecodeString();
            var faucetEnabled = rlpStream.DecodeBool();
            var faucetAddress = rlpStream.DecodeString();
            var faucetHost = rlpStream.DecodeString();
            var faucetWeiRequestMaxValue = rlpStream.DecodeUInt256();
            var faucetEthDailyRequestsTotalValue = rlpStream.DecodeUInt256();
            var consumerAddress = rlpStream.DecodeString();
            var contractAddress = rlpStream.DecodeString();
            var providerName = rlpStream.DecodeString();
            var providerAddress = rlpStream.DecodeString();
            var providerColdWalletAddress = rlpStream.DecodeString();
            var receiptRequestThreshold = rlpStream.DecodeUInt256();
            var receiptsMergeThreshold = rlpStream.DecodeUInt256();
            var paymentClaimThreshold = rlpStream.DecodeUInt256();
            var blockConfirmations = rlpStream.DecodeUInt();
            var filesPath = rlpStream.DecodeString();
            var fileMaxSize = rlpStream.DecodeUlong();
            var pluginsPath = rlpStream.DecodeString();
            var databasePath = rlpStream.DecodeString();
            var proxyEnabled = rlpStream.DecodeBool();
            var jsonRpcUrlProxies = rlpStream.DecodeArray(c => c.DecodeString());
            var gasPriceType = rlpStream.DecodeString();
            var gasPrice = rlpStream.DecodeUInt256();
            var cancelTransactionGasPricePercentageMultiplier = rlpStream.DecodeUInt();
            var jsonRpcDataChannelEnabled = rlpStream.DecodeBool();

            return new NdmConfig
            {
                Enabled = enabled,
                Id = id,
                InitializerName = initializerName,
                StoreConfigInDatabase = storeConfigInDatabase,
                VerifyP2PSignature = verifyP2PSignature,
                Persistence = persistence,
                FaucetEnabled = faucetEnabled,
                FaucetAddress = faucetAddress,
                FaucetHost = faucetHost,
                FaucetWeiRequestMaxValue = faucetWeiRequestMaxValue,
                FaucetEthDailyRequestsTotalValue = faucetEthDailyRequestsTotalValue,
                ConsumerAddress = consumerAddress,
                ContractAddress = contractAddress,
                ProviderName = providerName,
                ProviderAddress = providerAddress,
                ProviderColdWalletAddress = providerColdWalletAddress,
                ReceiptRequestThreshold = receiptRequestThreshold,
                ReceiptsMergeThreshold = receiptsMergeThreshold,
                PaymentClaimThreshold = paymentClaimThreshold,
                BlockConfirmations = blockConfirmations,
                FilesPath = filesPath,
                FileMaxSize = fileMaxSize,
                PluginsPath = pluginsPath,
                DatabasePath = databasePath,
                ProxyEnabled = proxyEnabled,
                JsonRpcUrlProxies = jsonRpcUrlProxies,
                GasPriceType = gasPriceType,
                GasPrice = gasPrice,
                CancelTransactionGasPricePercentageMultiplier = cancelTransactionGasPricePercentageMultiplier,
                JsonRpcDataChannelEnabled = jsonRpcDataChannelEnabled
            };
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
                Serialization.Rlp.Rlp.Encode(item.JsonRpcDataChannelEnabled));
        }

        public void Encode(MemoryStream stream, NdmConfig item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(NdmConfig item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}