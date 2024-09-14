// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using System;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Multiformats.Address;
using Nethermind.Shutter.Config;
using Nethermind.Logging;
using Nethermind.Core.Extensions;
using ILogger = Nethermind.Logging.ILogger;
using System.Threading.Channels;
using Google.Protobuf;

namespace Nethermind.Shutter;

public class ShutterP2P : IShutterP2P
{
    private readonly ILogger _logger;
    private readonly IShutterConfig _cfg;
    private readonly Channel<byte[]> _msgQueue = Channel.CreateBounded<byte[]>(1000);
    private readonly PubsubRouter _router;
    private readonly ILocalPeer _peer;
    private readonly ServiceProvider _serviceProvider;
    private CancellationTokenSource? _cts;
    private static readonly TimeSpan DisconnectionLogTimeout = TimeSpan.FromMinutes(5);

    public class ShutterP2PException(string message, Exception? innerException = null) : Exception(message, innerException);

    public event EventHandler<IShutterP2P.KeysReceivedArgs>? KeysReceived;

    public ShutterP2P(IShutterConfig shutterConfig, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _cfg = shutterConfig;
        _serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = shutterConfig.P2PProtocolVersion,
                AgentVersion = shutterConfig.P2PAgentVersion
            })
            // pubsub settings
            .AddSingleton(new Settings()
            {
                ReconnectionAttempts = int.MaxValue,
                Degree = 3,
                LowestDegree = 2,
                HighestDegree = 6,
                LazyDegree = 3
            })
            //.AddSingleton<ILoggerFactory>(new NethermindLoggerFactory(logManager))
            // .AddLogging(builder =>
            //     builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace)
            //     .AddSimpleConsole(l =>
            //     {
            //         l.SingleLine = true;
            //         l.TimestampFormat = "[HH:mm:ss.FFF]";
            //     })
            // )
            .BuildServiceProvider();

        IPeerFactory peerFactory = _serviceProvider!.GetService<IPeerFactory>()!;
        _peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + _cfg.P2PPort);
        _router = _serviceProvider!.GetService<PubsubRouter>()!;
        ITopic topic = _router.Subscribe("decryptionKeys");

        topic.OnMessage += (byte[] msg) =>
        {
            _msgQueue.Writer.TryWrite(msg);
            if (_logger.IsTrace) _logger.Trace("Received Shutter P2P message.");
        };
    }

    public Task Start(CancellationTokenSource? cts = null)
    {
        MyProto proto = new();
        _cts = cts ?? new();
        _ = _router!.RunAsync(_peer, proto, token: _cts.Token);
        proto.SetupFinished().GetAwaiter().GetResult();
        ConnectToPeers(proto, _cfg.KeyperP2PAddresses!);

        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {_peer.Address}");

        return Task.Run(async () =>
        {
            long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
            while (true)
            {
                try
                {
                    using var timeoutSource = new CancellationTokenSource(DisconnectionLogTimeout);
                    using var source = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutSource.Token);

                    byte[] msg = await _msgQueue.Reader.ReadAsync(source.Token);
                    lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                    ProcessP2PMessage(msg);
                }
                catch (OperationCanceledException)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        if (_logger.IsInfo) _logger.Info($"Shutting down Shutter P2P...");
                        break;
                    }
                    else if (_logger.IsWarn)
                    {
                        long delta = DateTimeOffset.Now.ToUnixTimeSeconds() - lastMessageProcessed;
                        _logger.Warn($"Not receiving Shutter messages ({delta / 60}m)...");
                    }
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Shutter processing thread error", e);
                    throw new ShutterP2PException("Shutter processing thread error", e);
                }
            }
        }, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _router?.UnsubscribeAll();
        await (_serviceProvider?.DisposeAsync() ?? default);
        await (_cts?.CancelAsync() ?? Task.CompletedTask);
    }

    private class MyProto : IDiscoveryProtocol
    {
        private readonly TaskCompletionSource taskCompletionSource = new();
        public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
        public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

        public Task SetupFinished() => taskCompletionSource.Task;

        public Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
        {
            taskCompletionSource.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private void ProcessP2PMessage(byte[] msg)
    {
        if (_logger.IsTrace) _logger.Trace("Processing Shutter P2P message.");

        try
        {
            Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
            if (envelope.Message.TryUnpack(out Dto.DecryptionKeys decryptionKeys))
            {
                KeysReceived?.Invoke(this, new(decryptionKeys));
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug($"Could not parse Shutter decryption keys: protobuf type names did not match.");
            }
        }
        catch (InvalidProtocolBufferException e)
        {
            if (_logger.IsDebug) _logger.Warn($"Could not parse Shutter decryption keys: {e}");
        }
    }

    private static void ConnectToPeers(MyProto proto, IEnumerable<string> p2pAddresses)
    {
        // shuffle peers to connect to random subset of keypers
        int seed = (int)(DateTimeOffset.Now.ToUnixTimeSeconds() % int.MaxValue);
        foreach (string addr in p2pAddresses.Shuffle(new Random(seed)))
        {
            proto.OnAddPeer?.Invoke([addr]);
        }
    }
}
