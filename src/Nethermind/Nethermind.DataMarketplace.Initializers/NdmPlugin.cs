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
    public class NdmPlugin : IPlugin
    {
        private readonly INdmInitializer _ndmInitializer;

        public NdmPlugin(INdmInitializer ndmInitializer)
        {
            _ndmInitializer = ndmInitializer;
        }

        Task IPlugin.Init(INethermindApi api)
        {
            throw new NotImplementedException();
        }

        public async Task InitNetworkProtocol(INethermindApi api)
        {
            ILogger logger = api.LogManager.GetClassLogger();
            if (logger.IsInfo) logger.Info($"Initializing NDM...");

            INdmApi ndmApi = new NdmApi(api);
            ndmApi.HttpClient = new DefaultHttpClient(new HttpClient(), api.EthereumJsonSerializer, api.LogManager);
            INdmConfig ndmConfig = api.Config<INdmConfig>();
            if (ndmConfig.ProxyEnabled)
            {
                api.JsonRpcClientProxy = new JsonRpcClientProxy(ndmApi.HttpClient, ndmConfig.JsonRpcUrlProxies,
                    api.LogManager);
                api.EthJsonRpcClientProxy = new EthJsonRpcClientProxy(api.JsonRpcClientProxy);
            }
            
            INdmCapabilityConnector capabilityConnector = await _ndmInitializer.InitAsync(ndmApi);

            capabilityConnector.Init();
            if (logger.IsInfo) logger.Info($"NDM initialized.");
        }

        public Task InitRpcModules(INethermindApi api)
        {
            ILogger logger = api.LogManager.GetClassLogger();
            
            // TODO: ensure we can override during Register calls
            if (api.Config<INdmConfig>().ProxyEnabled)
            {
                EthModuleProxyFactory proxyFactory = new EthModuleProxyFactory(
                    api.EthJsonRpcClientProxy,
                    api.Wallet);
                api.RpcModuleProvider.Register(new SingletonModulePool<IEthModule>(proxyFactory, true));
                if (logger.IsInfo) logger.Info("Enabled JSON RPC Proxy for NDM.");
            }

            return Task.CompletedTask;
        }

        public string Name { get; }
        public string Description { get; }
        public string Author { get; }

        public void Init(INethermindApi api)
        {
            // TODO: load messages nicely?
            api.MessageSerializationService.Register(Assembly.GetAssembly(typeof(HiMessageSerializer)));

            ILogger logger = api.LogManager.GetClassLogger();
            INdmInitializer? ndmInitializer = null;
            INdmConfig ndmConfig = api.ConfigProvider.GetConfig<INdmConfig>();
            bool ndmEnabled = ndmConfig.Enabled;
            if (ndmEnabled)
            {
                INdmDataPublisher? ndmDataPublisher = new NdmDataPublisher();
                INdmConsumerChannelManager? ndmConsumerChannelManager = new NdmConsumerChannelManager();
                string initializerName = ndmConfig.InitializerName;
                if (logger?.IsInfo ?? false) logger!.Info($"NDM initializer: {initializerName}");
                Type ndmInitializerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t =>
                        t.GetCustomAttribute<NdmInitializerAttribute>()?.Name == initializerName);

                NdmModule ndmModule = new NdmModule();
                NdmConsumersModule ndmConsumersModule = new NdmConsumersModule();
                ndmInitializer =
                    new NdmInitializerFactory(ndmInitializerType, ndmModule, ndmConsumersModule, api.LogManager)
                        .CreateOrFail();

                if (api.GrpcServer != null)
                {
                    var grpcChannel = new GrpcNdmConsumerChannel(api.GrpcServer);
                    ndmConsumerChannelManager.Add(grpcChannel);
                }

                NdmWebSocketsModule ndmWebSocketsModule =
                    new NdmWebSocketsModule(ndmConsumerChannelManager, ndmDataPublisher, api.EthereumJsonSerializer);
                api.WebSocketsManager.AddModule(ndmWebSocketsModule);
            }
        }

        public void Dispose()
        {
        }
    }
}