using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.DataMarketplace.Providers.Plugins.Yaml;
using Nethermind.DataMarketplace.Providers.Services;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Providers.Test")]
namespace Nethermind.DataMarketplace.Providers.Infrastructure
{
    [NdmInitializer("ndm-provider")]
    internal class NdmProviderInitializer : NdmInitializer
    {
        private NdmProvidersModule providersModule;
        private readonly INdmConsumersModule _consumerModule;

        public NdmProviderInitializer(INdmModule ndmModule, INdmConsumersModule ndmConsumersModule, ILogManager logManager) : base(ndmModule,
            ndmConsumersModule, logManager)
        {
            _consumerModule = ndmConsumersModule;
        }
        
        public override async Task<INdmCapabilityConnector> InitAsync(INdmApi ndmApi)
        {
            INdmConfig ndmConfig = ndmApi.ConfigProvider.GetConfig<INdmConfig>();
            await PreInitAsync(ndmApi);

            if (!ndmConfig.Enabled)
            {
                return NullNdmCapabilityConnector.Instance;
            }

            providersModule = new NdmProvidersModule(ndmApi);
            await _consumerModule.Init();
            await providersModule.InitAsync();
            IProviderService providerService = providersModule.GetProviderService();

            var subprotocolFactory = new NdmProviderSubprotocolFactory(ndmApi.MessageSerializationService, ndmApi.NodeStatsManager,
                ndmApi.LogManager, ndmApi.AccountService, ndmApi.ConsumerService, providerService, ndmApi.NdmConsumerChannelManager, ndmApi.EthereumEcdsa,
                ndmApi.Wallet, ndmApi.NdmFaucet, ndmApi.Enode.PublicKey, ndmApi.ProviderAddress, ndmApi.ConsumerAddress, ndmConfig.VerifyP2PSignature);
            var protocolHandlerFactory = new ProtocolHandlerFactory(subprotocolFactory, ndmApi.ProtocolValidator,
                ndmApi.EthRequestService, ndmApi.LogManager);
            var capabilityConnector = new NdmCapabilityConnector(ndmApi.ProtocolsManager, protocolHandlerFactory,
                ndmApi.AccountService, ndmApi.LogManager, ndmApi.ProviderAddress);
            providerService.AddressChanged += (_, e) =>
            {
                if (!(e.OldAddress is null) && e.OldAddress != Address.Zero)
                {
                    return;
                }

                capabilityConnector.AddCapability();
            };

            var pluginBuilder = new YamlNdmPluginBuilder();
            var pluginLoader = new YamlNdmPluginLoader("ndm-plugins", pluginBuilder, ndmApi.LogManager);
            var plugins = pluginLoader.Load();

            foreach (var plugin in plugins)
            {
                await providerService.InitPluginAsync(plugin);
            }

            ndmApi.MonitoringService?.RegisterMetrics(typeof(Metrics));

            return capabilityConnector;
        }

        public override void InitRpcModules()
        {
            providersModule.InitRpcModule();
            _consumerModule.InitRpcModules();
        }
    }
}