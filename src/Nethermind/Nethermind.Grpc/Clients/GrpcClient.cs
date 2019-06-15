using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Grpc.Clients
{
    public class GrpcClient : IGrpcClient
    {
        private readonly IJsonSerializer _jsonSerializer;
        private static readonly string NewLine = Environment.NewLine;
        private bool _stopped;
        private readonly string _displayName;
        private readonly IEnumerable<string> _acceptedHeaders;
        private readonly ILogger _logger;
        private readonly NdmExtension _extension;
        private Channel _channel;
        private NethermindService.NethermindServiceClient _client;
        private readonly string _address;

        public GrpcClient(IGrpcClientConfig config, IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _jsonSerializer = jsonSerializer;
            _displayName = config.DisplayName;
            _logger = logManager.GetClassLogger();
            _acceptedHeaders = (Regex.Replace(config.AcceptedHeaders ?? string.Empty,
                @"\s+", string.Empty)).Split(',');
            _extension = new NdmExtension
            {
                Name = config.Name,
                Type = config.Type,
                AcceptAllHeaders = config.AcceptAllHeaders,
                AcceptedHeaders = {_acceptedHeaders}
            };
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
            if (_logger.IsInfo) _logger.Info($"Connecting GRPC client for '{_displayName}' extension to {_address}...");
            _channel = new Channel(_address, ChannelCredentials.Insecure);
            _client = new NethermindService.NethermindServiceClient(_channel);
            var stream = _client.InitNdmExtension(_extension, new CallOptions().WithWaitForReady());
            while (_channel.State != ChannelState.Ready)
            {
                await Task.Delay(1000);
            }
            
            if (_logger.IsInfo) _logger.Info($"Connected GRPC client for '{_displayName}' extension to {_address}.{NewLine}Name: {_extension.Name}{NewLine}Type: {_extension.Type}" + $"{NewLine}Accept all headers: {_extension.AcceptAllHeaders}{NewLine}Accepted headers: {string.Join(", ", _extension.AcceptedHeaders)}");

            while (!_stopped && _channel.State == ChannelState.Ready && await stream.ResponseStream.MoveNext())
            {
                var query = stream.ResponseStream.Current;
                if (_logger.IsInfo) _logger.Info($"Received query for header: '{query.HeaderId}', deposit: '{query.DepositId}', args: {query.Args}, iterations: {query.Iterations}");
                await _client.SendNdmDataAsync(new NdmQueryData {Query = query, IsValid = true, Data = {new NdmData()}});
            }
        }

        public Task StopAsync()
        {
            _stopped = true;
            return _channel.ShutdownAsync();
        }

        public Task PublishAsync<T>(T data)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }

            if (_channel.State != ChannelState.Ready)
            {
                return Task.CompletedTask;
            }

            if (!(data is FullTransaction transaction))
            {
                return Task.CompletedTask;
            }

            return _acceptedHeaders.Any() ? TryPublishAsync(transaction.Receipt) : Task.CompletedTask;
        }

        private async Task TryPublishAsync<T>(T data)
        {
            try
            {
                var value = _jsonSerializer.Serialize(data);
                foreach (var headerId in _acceptedHeaders)
                {
                    if (_stopped || _channel.State != ChannelState.Ready)
                    {
                        return;
                    }
                    
                    await _client.SendNdmDataAsync(new NdmQueryData
                    {
                        Query = new NdmQuery
                        {
                            HeaderId = headerId
                        },
                        IsValid = true,
                        Data =
                        {
                            new NdmData
                            {
                                Value = value
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error(ex.ToString(), ex);
            }
        }
    }
}