using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Core.Specs;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Network;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Initializers
{
    public interface INdmInitializer
    {
        Task<INdmCapabilityConnector> InitAsync(IConfigProvider configProvider, IDbProvider dbProvider,
            IBlockProcessor blockProcessor, IBlockTree blockTree, ITxPool txPool,
            ITxPoolInfoProvider txPoolInfoProvider, ISpecProvider specProvider, IReceiptStorage receiptStorage,
            IWallet wallet, ITimestamp timestamp, IEcdsa ecdsa, IRpcModuleProvider rpcModuleProvider,
            IKeyStore keyStore, IJsonSerializer jsonSerializer, ICryptoRandom cryptoRandom, IEnode enode,
            INdmConsumerChannelManager consumerChannelManager, INdmDataPublisher dataPublisher,
            IGrpcService grpcService, INodeStatsManager nodeStatsManager, IProtocolsManager protocolsManager,
            IProtocolValidator protocolValidator, IMessageSerializationService messageSerializationService,
            ILogManager logManager);
    }
}