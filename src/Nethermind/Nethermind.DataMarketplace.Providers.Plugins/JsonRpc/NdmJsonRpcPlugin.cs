using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Plugins.JsonRpc
{
    public class NdmJsonRpcPlugin : INdmJsonRpcPlugin
    {
        public string? Name { get; private set; }
        public string? Type { get; private set; }
        public string? Host { get; private set; } 
        public IJsonRpcClient? JsonRpcClient { get; set; }
        private ILogger? _logger;

        public Task InitAsync(ILogManager logManager)
        {
            if(string.IsNullOrEmpty(Host))
            {
                throw new ArgumentNullException($"Host ip was not specified for NDM plugin: {Name}");
            }

            _logger = logManager.GetClassLogger();
            if(_logger.IsInfo) _logger.Info($"Initialized NDM JsonRpc plugin: {Name}, Host: {Host}");

            if(JsonRpcClient == null)
            {
                JsonRpcClient = new JsonRpcClient(Host, _logger);
            }
            
            return Task.CompletedTask;
        }

        public async Task<string?> QueryAsync(IEnumerable<string> args)
        {
            if(args is null)
            {
                if(_logger.IsError) _logger.Error($"No data given in query from consumer for host: ${Host}");
            }

            var result = JsonRpcClient.PostAsync(args.First());

            return result;
        }

        //not needed in this plugin
        public Task SubscribeAsync(Action<string> callback, Func<bool> enabled, IEnumerable<string> args, CancellationToken? token = null)
        {
            return Task.CompletedTask;
        }
    }
}