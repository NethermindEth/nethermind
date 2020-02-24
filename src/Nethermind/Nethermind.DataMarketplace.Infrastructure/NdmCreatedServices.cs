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
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure
{
    public class NdmCreatedServices
    {
        public Address ConsumerAddress { get; }
        public IAbiEncoder AbiEncoder { get; }
        public IRlpDecoder<DataAsset> DataAssetRlpDecoder { get; }
        public IDepositService DepositService { get; }
        public GasPriceService GasPriceService { get; }
        public TransactionService TransactionService { get; }
        public INdmDataPublisher NdmDataPublisher { get; }
        public IJsonRpcNdmConsumerChannel JsonRpcNdmConsumerChannel { get; }
        public INdmConsumerChannelManager NdmConsumerChannelManager { get; }
        public INdmBlockchainBridge BlockchainBridge { get; }

        public NdmCreatedServices(
            Address consumerAddress,
            IAbiEncoder abiEncoder,
            IRlpDecoder<DataAsset> dataAssetRlpDecoder,
            IDepositService depositService,
            GasPriceService gasPriceService,
            TransactionService transactionService,
            INdmDataPublisher ndmDataPublisher,
            IJsonRpcNdmConsumerChannel jsonRpcNdmConsumerChannel,
            INdmConsumerChannelManager ndmConsumerChannelManager,
            INdmBlockchainBridge blockchainBridge)
        {
            ConsumerAddress = consumerAddress ?? throw new ArgumentNullException(nameof(consumerAddress));
            AbiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            DataAssetRlpDecoder = dataAssetRlpDecoder ?? throw new ArgumentNullException(nameof(dataAssetRlpDecoder));
            DepositService = depositService ?? throw new ArgumentNullException(nameof(depositService));
            GasPriceService = gasPriceService ?? throw new ArgumentNullException(nameof(gasPriceService));
            TransactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
            NdmDataPublisher = ndmDataPublisher ?? throw new ArgumentNullException(nameof(ndmDataPublisher));
            JsonRpcNdmConsumerChannel = jsonRpcNdmConsumerChannel ?? throw new ArgumentNullException(nameof(jsonRpcNdmConsumerChannel));
            NdmConsumerChannelManager = ndmConsumerChannelManager ?? throw new ArgumentNullException(nameof(ndmConsumerChannelManager));
            BlockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
        }
    }
}