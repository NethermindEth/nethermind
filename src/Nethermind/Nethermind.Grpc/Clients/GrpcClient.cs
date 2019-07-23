using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Logging;

namespace Nethermind.Grpc.Clients
{
    public class GrpcClient : IGrpcClient
    {
        private static readonly string NewLine = Environment.NewLine;
        private bool _connected;
        private readonly ILogger _logger;
        private Channel _channel;
        private NethermindService.NethermindServiceClient _client;
        private readonly string _address;

        public GrpcClient(string host, int port, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _address = $"{host}:{port}";
        }

        public async Task StartAsync()
        {
            try
            {
                await TryStartAsync();
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"There was an error:{NewLine}{ex}{NewLine}");
            }
        }

        private async Task TryStartAsync()
        {
            if (_logger.IsInfo) _logger.Info($"Connecting GRPC client to: '{_address}'...");
            _channel = new Channel(_address, ChannelCredentials.Insecure);
            await _channel.ConnectAsync();
            _client = new NethermindService.NethermindServiceClient(_channel);
            while (_channel.State != ChannelState.Ready)
            {
                await Task.Delay(1000);
            }

            if (_logger.IsInfo) _logger.Info($"Connected GRPC client to: '{_address}'");
            _connected = true;
        }

        public Task StopAsync()
        {
            _connected = false;
            return _channel?.ShutdownAsync() ?? Task.CompletedTask;
        }

        public async Task<string> QueryAsync(params string[] args)
        {
            if (!_connected)
            {
                return string.Empty;
            }

            var queryArgs = args ?? Array.Empty<string>();
            var result = await _client.QueryAsync(new QueryRequest
            {
                Args = {queryArgs}
            });

            return result.Data;
        }

        public async Task SubscribeAsync(Action<string> callback, params string[] args)
        {
            if (!_connected)
            {
                return;
            }

            var streamArgs = args ?? Array.Empty<string>();
            using (var stream = _client.Subscribe(new SubscriptionRequest
            {
                Args = {streamArgs}
            }))
            {
                while (_connected && await stream.ResponseStream.MoveNext())
                {
                    callback(stream.ResponseStream.Current.Data);
                }
            }
        }
    }
}