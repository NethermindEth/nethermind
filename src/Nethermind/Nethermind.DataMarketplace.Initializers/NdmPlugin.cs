using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Channels.Grpc;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.DataMarketplace.Subprotocols.Serializers;
using Nethermind.DataMarketplace.WebSockets;
using Nethermind.Facade.Proxy;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Initializers
{
    public class NdmPlugin : INethermindPlugin
    {
        private INdmInitializer? _ndmInitializer;

        private INdmApi? _ndmApi;

        public async Task InitNetworkProtocol()
        {
            if (_ndmInitializer == null)
            {
                throw new InvalidOperationException(
                    $"Cannot {nameof(InitNetworkProtocol)} in NDM before preparing an NDM initializer.");
            }
            
            ILogger logger = _ndmApi.LogManager.GetClassLogger();
            if (logger.IsInfo) logger.Info("Initializing NDM network protocol...");
            
            _ndmApi.HttpClient = new DefaultHttpClient(new HttpClient(), _ndmApi.EthereumJsonSerializer, _ndmApi.LogManager);
            INdmConfig ndmConfig = _ndmApi.Config<INdmConfig>();
            if (ndmConfig.ProxyEnabled)
            {
                _ndmApi.JsonRpcClientProxy = new JsonRpcClientProxy(
                    _ndmApi.HttpClient,
                    ndmConfig.JsonRpcUrlProxies,
                    _ndmApi.LogManager);
                _ndmApi.EthJsonRpcClientProxy = new EthJsonRpcClientProxy(_ndmApi.JsonRpcClientProxy);
            }
            
            INdmCapabilityConnector capabilityConnector = await _ndmInitializer.InitAsync(_ndmApi);

            capabilityConnector.Init();
            if (logger.IsInfo) logger.Info("NDM network protocol initialized.");
        }

        public Task InitRpcModules()
        {
            ILogger logger = _ndmApi.LogManager.GetClassLogger();
            
            // TODO: ensure we can override during Register calls
            if (_ndmApi.Config<INdmConfig>().ProxyEnabled)
            {
                EthModuleProxyFactory proxyFactory = new EthModuleProxyFactory(
                    _ndmApi.EthJsonRpcClientProxy,
                    _ndmApi.Wallet);
                _ndmApi.RpcModuleProvider.Register(new SingletonModulePool<IEthModule>(proxyFactory, true));
                if (logger.IsInfo) logger.Info("Enabled JSON RPC Proxy for NDM.");
            }

            return Task.CompletedTask;
        }

        public string Name => "NDM";
        public string Description => "Nethermind Data Marketplace";
        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _ndmApi = new NdmApi(api);
            // TODO: load messages nicely?
            api.MessageSerializationService.Register(Assembly.GetAssembly(typeof(HiMessageSerializer)));

            ILogger logger = api.LogManager.GetClassLogger();
            INdmConfig ndmConfig = api.ConfigProvider.GetConfig<INdmConfig>();
            bool ndmEnabled = ndmConfig.Enabled;
            if (ndmEnabled)
            {
                INdmDataPublisher? ndmDataPublisher = new NdmDataPublisher();
                INdmConsumerChannelManager? ndmConsumerChannelManager = new NdmConsumerChannelManager();
                string initializerName = ndmConfig.InitializerName;
                if (logger.IsInfo) logger.Info($"NDM initializer: {initializerName}");
                Type? ndmInitializerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t =>
                        t.GetCustomAttribute<NdmInitializerAttribute>()?.Name == initializerName);
                if (ndmInitializerType == null)
                {
                    if(logger.IsError) logger.Error(
                        $"NDM enabled but the initializer {initializerName} has not been found. Ensure that a plugin exists with the properly set {nameof(NdmInitializerAttribute)}");
                }

                NdmModule ndmModule = new NdmModule();
                NdmConsumersModule ndmConsumersModule = new NdmConsumersModule();
                _ndmInitializer =
                    new NdmInitializerFactory(ndmInitializerType, ndmModule, ndmConsumersModule, api.LogManager)
                        .CreateOrFail();

                if (api.GrpcServer != null)
                {
                    var grpcChannel = new GrpcNdmConsumerChannel(api.GrpcServer);
                    ndmConsumerChannelManager.Add(grpcChannel);
                }

                NdmWebSocketsModule ndmWebSocketsModule =
                    new NdmWebSocketsModule(
                        ndmConsumerChannelManager,
                        ndmDataPublisher,
                        api.EthereumJsonSerializer); 
                api.WebSocketsManager.AddModule(ndmWebSocketsModule);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}