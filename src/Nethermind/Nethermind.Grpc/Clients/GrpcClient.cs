// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Logging;

namespace Nethermind.Grpc.Clients
{
    public class GrpcClient : IGrpcClient
    {
        private readonly int _reconnectionInterval;
        private int _retry;
        private bool _connected;
        private readonly ILogger _logger;
        private Channel _channel;
        private NethermindService.NethermindServiceClient _client;
        private readonly string _address;

        public GrpcClient(string host, int port, int reconnectionInterval, ILogManager logManager)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Missing gRPC host.", nameof(host));
            }

            if (port < 1 || port > 65535)
            {
                throw new ArgumentException($"Invalid gRPC port: {port}.", nameof(port));
            }

            if (reconnectionInterval < 0)
            {
                throw new ArgumentException($"Invalid reconnection interval: {reconnectionInterval} ms.",
                    nameof(reconnectionInterval));
            }

            _address = string.IsNullOrWhiteSpace(host)
                ? throw new ArgumentException("Missing gRPC host", nameof(host))
                : $"{host}:{port}";
            _reconnectionInterval = reconnectionInterval;
            _logger = logManager.GetClassLogger();
        }

        public async Task StartAsync()
        {
            try
            {
                await TryStartAsync();
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error(ex.Message, ex);
            }
        }

        private async Task TryStartAsync()
        {
            if (_logger.IsInfo) _logger.Info($"Connecting gRPC client to: '{_address}'...");
            _channel = new Channel(_address, ChannelCredentials.Insecure);
            await _channel.ConnectAsync();
            _client = new NethermindService.NethermindServiceClient(_channel);
            while (_channel.State != ChannelState.Ready)
            {
                await Task.Delay(_reconnectionInterval);
            }

            if (_logger.IsInfo) _logger.Info($"Connected gRPC client to: '{_address}'");
            _connected = true;
        }

        public Task StopAsync()
        {
            _connected = false;
            return _channel?.ShutdownAsync() ?? Task.CompletedTask;
        }

        public async Task<string> QueryAsync(IEnumerable<string> args)
        {
            try
            {
                if (!_connected)
                {
                    return string.Empty;
                }

                var result = await _client.QueryAsync(new QueryRequest
                {
                    Args = { args ?? Enumerable.Empty<string>() }
                });

                return result.Data;
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error(ex.Message, ex);
                await TryReconnectAsync();
                return string.Empty;
            }
        }

        public async Task SubscribeAsync(Action<string> callback, Func<bool> enabled, IEnumerable<string> args,
            CancellationToken? token = null)
        {
            var cancellationToken = token ?? CancellationToken.None;
            try
            {
                if (!_connected)
                {
                    return;
                }

                using (var stream = _client.Subscribe(new SubscriptionRequest
                {
                    Args = { args ?? Enumerable.Empty<string>() }
                }))
                {
                    while (enabled() && _connected && !cancellationToken.IsCancellationRequested &&
                           await stream.ResponseStream.MoveNext(cancellationToken))
                    {
                        callback(stream.ResponseStream.Current.Data);
                    }
                }
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (_logger.IsError) _logger.Error(ex.Message, ex);
                await TryReconnectAsync();
            }
        }

        private async Task TryReconnectAsync()
        {
            _connected = false;
            _retry++;
            if (_logger.IsWarn) _logger.Warn($"Retrying ({_retry}) gRPC connection to: '{_address}' in {_reconnectionInterval} ms.");
            await Task.Delay(_reconnectionInterval);
            await StartAsync();
        }
    }
}
