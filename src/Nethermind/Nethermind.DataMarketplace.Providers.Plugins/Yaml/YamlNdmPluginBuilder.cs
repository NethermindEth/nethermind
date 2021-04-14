using Nethermind.DataMarketplace.Providers.Plugins.Grpc;
using Nethermind.DataMarketplace.Providers.Plugins.JsonRpc;
using Nethermind.DataMarketplace.Providers.Plugins.WebApi;
using Nethermind.DataMarketplace.Providers.Plugins.WebSockets;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Nethermind.DataMarketplace.Providers.Plugins.Yaml
{
    public class YamlNdmPluginBuilder : INdmPluginBuilder
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        public INdmPlugin? Build(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            var plugin = Deserializer.Deserialize<PluginInfo>(description);
            switch (plugin.Type?.ToLowerInvariant())
            {
                case "webapi": return Deserializer.Deserialize<NdmWebApiPlugin>(description);
                case "websockets": return Deserializer.Deserialize<NdmWebSocketsPlugin>(description);
                case "grpc": return Deserializer.Deserialize<NdmGrpcPlugin>(description);
                case "jsonrpc": return Deserializer.Deserialize<NdmJsonRpcPlugin>(description);
            }

            return null;
        }

        private class PluginInfo
        {
            public string? Type { get; set; }
        }
    }
}