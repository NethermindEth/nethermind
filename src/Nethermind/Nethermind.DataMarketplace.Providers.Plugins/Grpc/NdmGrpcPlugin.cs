using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Grpc;
using Nethermind.Grpc.Clients;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Plugins.Grpc
{
    public class NdmGrpcPlugin : INdmGrpcPlugin
    {
        private bool _initialized;
        private IGrpcClient? _client;
        private ILogger? _logger;
        public string? Name { get; private set; }
        public string? Type { get; private set; }
        public string? Host { get; private set; }
        public int Port { get; private set; }
        public int ReconnectionInterval { get; private set; }

        public Task InitAsync(ILogManager logManager)
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                throw new Exception($"Host was not specified for NDM plugin: {Name}");
            }

            _logger = logManager.GetClassLogger();
            _initialized = true;
            _client = new GrpcClient(Host, Port, ReconnectionInterval, logManager);
            if (_logger.IsInfo) _logger.Info($"Initialized NDM gRPC plugin: {Name}, host: {Host}, port: {Port}");

            return _client.StartAsync();
        }

        public Task<string?> QueryAsync(IEnumerable<string> args)
        {
            if (_client == null)
            {
                throw new InvalidOperationException("Cannot query an uninitialized plugin");
            }

            return _initialized ? _client.QueryAsync(args) : Task.FromResult<string?>(string.Empty);
        }


        public Task SubscribeAsync(Action<string> callback, Func<bool> enabled, IEnumerable<string> args,
            CancellationToken? token = null)
        {
            if (_client == null)
            {
                throw new InvalidOperationException("Cannot subscribe to uninitialized plugin");    
            }
            
            return _initialized ? _client.SubscribeAsync(callback, enabled, args, token) : Task.CompletedTask;
        }
    }
}