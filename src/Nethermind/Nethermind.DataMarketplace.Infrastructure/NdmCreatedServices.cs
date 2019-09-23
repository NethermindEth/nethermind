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

using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Facade;
using Nethermind.Facade.Proxy;

namespace Nethermind.DataMarketplace.Infrastructure
{
    public class NdmCreatedServices
    {
        public Address ConsumerAddress { get; }
        public IAbiEncoder AbiEncoder { get; }
        public IRlpDecoder<DataAsset> DataAssetRlpDecoder { get; }
        public IDepositService DepositService { get; }
        public INdmDataPublisher NdmDataPublisher { get; }
        public IJsonRpcNdmConsumerChannel JsonRpcNdmConsumerChannel { get; }
        public INdmConsumerChannelManager NdmConsumerChannelManager { get; }
        public INdmBlockchainBridge BlockchainBridge { get; }
        public IEthJsonRpcClientProxy EthJsonRpcClientProxy { get; }

        public NdmCreatedServices(Address consumerAddress,
            IAbiEncoder abiEncoder, IRlpDecoder<DataAsset> dataAssetRlpDecoder, IDepositService depositService,
            INdmDataPublisher ndmDataPublisher, IJsonRpcNdmConsumerChannel jsonRpcNdmConsumerChannel,
            INdmConsumerChannelManager ndmConsumerChannelManager, INdmBlockchainBridge blockchainBridge,
            IEthJsonRpcClientProxy ethJsonRpcClientProxy)
        {
            ConsumerAddress = consumerAddress;
            AbiEncoder = abiEncoder;
            DataAssetRlpDecoder = dataAssetRlpDecoder;
            DepositService = depositService;
            NdmDataPublisher = ndmDataPublisher;
            JsonRpcNdmConsumerChannel = jsonRpcNdmConsumerChannel;
            NdmConsumerChannelManager = ndmConsumerChannelManager;
            BlockchainBridge = blockchainBridge;
            EthJsonRpcClientProxy = ethJsonRpcClientProxy;
        }
    }
}