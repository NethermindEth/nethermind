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

        public NdmConfig Decode(Nethermind.Core.Encoding.Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = context.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var enabled = context.DecodeBool();
            var id = context.DecodeString();
            var initializerName = context.DecodeString();
            var storeConfigInDatabase = context.DecodeBool();
            var verifyP2PSignature = context.DecodeBool();
            var persistence = context.DecodeString();
            var faucetEnabled = context.DecodeBool();
            var faucetAddress = context.DecodeString();
            var faucetHost = context.DecodeString();
            var faucetWeiRequestMaxValue = context.DecodeUInt256();
            var consumerAddress = context.DecodeString();
            var contractAddress = context.DecodeString();
            var providerName = context.DecodeString();
            var providerAddress = context.DecodeString();
            var providerColdWalletAddress = context.DecodeString();
            var receiptRequestThreshold = context.DecodeUInt256();
            var receiptsMergeThreshold = context.DecodeUInt256();
            var paymentClaimThreshold = context.DecodeUInt256();
            var blockConfirmations = context.DecodeUInt();
            var filesPath = context.DecodeString();
            var fileMaxSize = context.DecodeUlong();
            var pluginsPath = context.DecodeString();

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
                PluginsPath = pluginsPath
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
                Nethermind.Core.Encoding.Rlp.Encode(item.PluginsPath));
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