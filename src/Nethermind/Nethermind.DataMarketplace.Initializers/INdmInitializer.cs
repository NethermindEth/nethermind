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

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.Db;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Stats;
using Nethermind.Store.Bloom;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.Initializers
{
    public interface INdmInitializer
    {
        Task<INdmCapabilityConnector> InitAsync(
            IConfigProvider configProvider,
            IDbProvider dbProvider,
            string baseDbPath,
            IBlockTree blockTree,
            ITxPool txPool,
            ISpecProvider specProvider,
            IReceiptFinder receiptFinder,
            IWallet wallet,
            IFilterStore filterStore,
            IFilterManager filterManager,
            ITimestamper timestamper,
            IEthereumEcdsa ecdsa,
            IRpcModuleProvider rpcModuleProvider,
            IKeyStore keyStore,
            IJsonSerializer jsonSerializer,
            ICryptoRandom cryptoRandom,
            IEnode enode,
            INdmConsumerChannelManager consumerChannelManager,
            INdmDataPublisher dataPublisher,
            IGrpcServer grpcServer,
            INodeStatsManager nodeStatsManager,
            IProtocolsManager protocolsManager,
            IProtocolValidator protocolValidator,
            IMessageSerializationService messageSerializationService,
            bool enableUnsecuredDevWallet,
            IWebSocketsManager webSocketsManager,
            ILogManager logManager,
            IBlockProcessor blockProcessor,
            IJsonRpcClientProxy? jsonRpcClientProxy,
            IEthJsonRpcClientProxy? ethJsonRpcClientProxy,
            IHttpClient httpClient,
            IMonitoringService monitoringService,
            IBloomStorage bloomStorage);
    }
}