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
        private bool _stopped;
        private readonly ILogger _logger;
        private Channel _channel;
        private NethermindService.NethermindServiceClient _client;
        private readonly string _address;

        public GrpcClient(IGrpcClientConfig config, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _address = $"{config.Host}:{config.Port}";
        }

        public async Task StartAsync()
        {
            while (!_stopped)
            {
                try
                {
                    await TryStartAsync();
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"There was an error:{NewLine}{ex}{NewLine}");
                }
                finally
                {
                    await _channel.ShutdownAsync();
                }
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
        }

        public Task StopAsync()
        {
            _stopped = true;
            return _channel.ShutdownAsync();
        }

        public async Task<string> QueryAsync(params string[] args)
        {
            var queryArgs = args ?? Array.Empty<string>();
            var result = await _client.QueryAsync(new QueryRequest
            {
                Args = {args}
            });

            return result.Data;
        }

        public async Task SubscribeAsync(Action<string> callback, params string[] args)
        {
            var streamArgs = args ?? Array.Empty<string>();
            using (var stream = _client.Subscribe(new SubscriptionRequest
            {
                Args = {streamArgs}
            }))
            {
                while (await stream.ResponseStream.MoveNext())
                {
                    callback(stream.ResponseStream.Current.Data);
                }
            }
        }
    }
}