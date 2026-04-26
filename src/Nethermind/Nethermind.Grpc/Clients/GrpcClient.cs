// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core.Extensions;
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
#nullable enable
        private CancellationTokenSource? _cts = new();
#nullable restore

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

            _address = $"{host}:{port}";
            _reconnectionInterval = reconnectionInterval;
            _logger = logManager.GetClassLogger<GrpcClient>();
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
                await Task.Delay(_reconnectionInterval, _cts?.Token ?? CancellationToken.None);
            }

            if (_logger.IsInfo) _logger.Info($"Connected gRPC client to: '{_address}'");
            _connected = true;
        }

        public Task StopAsync()
        {
            _connected = false;
            CancellationTokenExtensions.CancelDisposeAndClear(ref _cts);
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

                QueryResponse result = await _client.QueryAsync(new QueryRequest
                {
                    Args = { args ?? [] }
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
            CancellationToken cancellationToken = token ?? CancellationToken.None;
            try
            {
                if (!_connected)
                {
                    return;
                }

                using AsyncServerStreamingCall<SubscriptionResponse> stream = _client.Subscribe(new SubscriptionRequest
                {
                    Args = { args ?? [] }
                });
                while (enabled() && _connected && !cancellationToken.IsCancellationRequested &&
                       await stream.ResponseStream.MoveNext(cancellationToken))
                {
                    callback(stream.ResponseStream.Current.Data);
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
            await StopAsync();
            _retry++;
            if (_logger.IsWarn) _logger.Warn($"Retrying ({_retry}) gRPC connection to: '{_address}' in {_reconnectionInterval} ms.");
            // Use CancellationToken.None: _cts is already cancelled by StopAsync, and this delay
            // represents backoff time before the next connection attempt.
            await Task.Delay(_reconnectionInterval, CancellationToken.None);
            await StartAsync();
        }
    }
}
