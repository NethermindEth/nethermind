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
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Core.Extensions;
using ILogger = Nethermind.Logging.ILogger;
using System.Threading.Channels;
using Google.Protobuf;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P(
    Action<Dto.DecryptionKeys> onDecryptionKeysReceived,
    IShutterConfig shutterConfig,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly Channel<byte[]> _msgQueue = Channel.CreateBounded<byte[]>(1000);
    private PubsubRouter? _router;
    private ServiceProvider? _serviceProvider;
    private CancellationTokenSource? _cancellationTokenSource;
    private static readonly TimeSpan DisconnectionLogTimeout = TimeSpan.FromMinutes(5);

    public class ShutterP2PException(string message, Exception? innerException = null) : Exception(message, innerException);

    public void Start(in IEnumerable<string> p2pAddresses)
    {
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

        IPeerFactory peerFactory = _serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + shutterConfig.P2PPort);
        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {peer.Address}");
        _router = _serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = _router.Subscribe("decryptionKeys");

        topic.OnMessage += (byte[] msg) =>
        {
            _msgQueue.Writer.TryWrite(msg);
            if (_logger.IsTrace) _logger.Trace("Received Shutter P2P message.");
        };

        MyProto proto = new();
        _cancellationTokenSource = new();
        _ = _router.RunAsync(peer, proto, token: _cancellationTokenSource.Token);
        proto.SetupFinished().GetAwaiter().GetResult();
        ConnectToPeers(proto, p2pAddresses);

        Task.Run(async () =>
        {
            long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
            while (true)
            {
                try
                {
                    using var timeoutSource = new CancellationTokenSource(DisconnectionLogTimeout);
                    using var source = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutSource.Token);

                    byte[] msg = await _msgQueue.Reader.ReadAsync(source.Token);
                    lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                    ProcessP2PMessage(msg);
                }
                catch (OperationCanceledException)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsInfo) _logger.Info($"Shutting down Shutter P2P...");
                        break;
                    }
                    else
                    {
                        long delta = DateTimeOffset.Now.ToUnixTimeSeconds() - lastMessageProcessed;
                        if (_logger.IsWarn) _logger.Warn($"Not receiving Shutter messages ({delta / 60}m)...");
                    }
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Shutter processing thread error", e);
                    throw new ShutterP2PException("Shutter processing thread error", e);
                }
            }
        }, _cancellationTokenSource.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _router?.UnsubscribeAll();
        await (_serviceProvider?.DisposeAsync() ?? default);
        await (_cancellationTokenSource?.CancelAsync() ?? Task.CompletedTask);
    }

    internal class MyProto : IDiscoveryProtocol
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

    internal void ProcessP2PMessage(byte[] msg)
    {
        if (_logger.IsTrace) _logger.Trace("Processing Shutter P2P message.");

        try
        {
            Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
            if (envelope.Message.TryUnpack(out Dto.DecryptionKeys decryptionKeys))
            {
                onDecryptionKeysReceived(decryptionKeys);
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Could not parse Shutter decryption keys: protobuf type names did not match.");
            }
        }
        catch (InvalidProtocolBufferException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Could not parse Shutter decryption keys: {e}");
        }
    }

    private void ConnectToPeers(MyProto proto, IEnumerable<string> p2pAddresses)
    {
        // shuffle peers to connect to random subset of keypers
        int seed = (int)(DateTimeOffset.Now.ToUnixTimeSeconds() % int.MaxValue);
        foreach (string addr in p2pAddresses.Shuffle(new Random(seed)))
        {
            proto.OnAddPeer?.Invoke([addr]);
        }
    }
}
