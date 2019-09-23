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

using System.IO;
using Nethermind.Core.Encoding;
using Nethermind.DataMarketplace.Core.Configs;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class NdmConfigDecoder : IRlpDecoder<NdmConfig>
    {
        public NdmConfigDecoder()
        {
        }

        static NdmConfigDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(NdmConfig)] = new NdmConfigDecoder();
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
            var proxyEnabled = rlpStream.DecodeBool();
            var jsonRpcUrlProxies = rlpStream.DecodeArray(c => c.DecodeString());

            return new NdmConfig
            {
                Enabled = enabled,
                Id = id,
                InitializerName =  initializerName,
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
                ProxyEnabled = proxyEnabled,
                JsonRpcUrlProxies = jsonRpcUrlProxies
            };
        }

        public Nethermind.Core.Encoding.Rlp Encode(NdmConfig item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Enabled),
                Nethermind.Core.Encoding.Rlp.Encode(item.Id),
                Nethermind.Core.Encoding.Rlp.Encode(item.InitializerName),
                Nethermind.Core.Encoding.Rlp.Encode(item.StoreConfigInDatabase),
                Nethermind.Core.Encoding.Rlp.Encode(item.VerifyP2PSignature),
                Nethermind.Core.Encoding.Rlp.Encode(item.Persistence),
                Nethermind.Core.Encoding.Rlp.Encode(item.FaucetEnabled),
                Nethermind.Core.Encoding.Rlp.Encode(item.FaucetAddress),
                Nethermind.Core.Encoding.Rlp.Encode(item.FaucetHost),
                Nethermind.Core.Encoding.Rlp.Encode(item.FaucetWeiRequestMaxValue),
                Nethermind.Core.Encoding.Rlp.Encode(item.FaucetEthDailyRequestsTotalValue),
                Nethermind.Core.Encoding.Rlp.Encode(item.ConsumerAddress),
                Nethermind.Core.Encoding.Rlp.Encode(item.ContractAddress),
                Nethermind.Core.Encoding.Rlp.Encode(item.ProviderName),
                Nethermind.Core.Encoding.Rlp.Encode(item.ProviderAddress),
                Nethermind.Core.Encoding.Rlp.Encode(item.ProviderColdWalletAddress),
                Nethermind.Core.Encoding.Rlp.Encode(item.ReceiptRequestThreshold),
                Nethermind.Core.Encoding.Rlp.Encode(item.ReceiptsMergeThreshold),
                Nethermind.Core.Encoding.Rlp.Encode(item.PaymentClaimThreshold),
                Nethermind.Core.Encoding.Rlp.Encode(item.BlockConfirmations),
                Nethermind.Core.Encoding.Rlp.Encode(item.FilesPath),
                Nethermind.Core.Encoding.Rlp.Encode(item.FileMaxSize),
                Nethermind.Core.Encoding.Rlp.Encode(item.PluginsPath),
                Nethermind.Core.Encoding.Rlp.Encode(item.ProxyEnabled),
                Nethermind.Core.Encoding.Rlp.Encode(item.JsonRpcUrlProxies));
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