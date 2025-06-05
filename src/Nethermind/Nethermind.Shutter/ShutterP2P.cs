// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;
using Nethermind.Network.Discovery;
using System;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Shutter.Config;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;
using System.Threading.Channels;
using Google.Protobuf;
using System.IO.Abstractions;
using Nethermind.KeyStore.Config;
using System.Net;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using System.Collections.Generic;

namespace Nethermind.Shutter;

public class ShutterP2P : IShutterP2P
{
    private readonly ILogger _logger;
    private readonly IShutterConfig _cfg;
    private readonly Channel<byte[]> _msgQueue = System.Threading.Channels.Channel.CreateBounded<byte[]>(1000);
    private readonly PubsubRouter _router;
    private readonly PubsubPeerDiscoveryProtocol _disc;
    private readonly PeerStore _peerStore;
    private readonly ILocalPeer _peer;
    private readonly string _address;
    private readonly ServiceProvider _serviceProvider;
    private readonly TimeSpan DisconnectionLogTimeout;
    private readonly TimeSpan DisconnectionLogInterval;

    public class ShutterP2PException(string message, Exception? innerException = null) : Exception(message, innerException);


    public ShutterP2P(IShutterConfig shutterConfig, ILogManager logManager, IFileSystem fileSystem, IKeyStoreConfig keyStoreConfig, IPAddress ip)
    {
        _logger = logManager.GetClassLogger();
        _cfg = shutterConfig;
        _address = $"/ip4/{ip}/tcp/{_cfg.P2PPort}";
        DisconnectionLogTimeout = TimeSpan.FromMilliseconds(_cfg.DisconnectionLogTimeout);
        DisconnectionLogInterval = TimeSpan.FromMilliseconds(_cfg.DisconnectionLogInterval);

        IServiceCollection serviceCollection = new ServiceCollection()
            .AddLibp2p(builder => builder.WithPubsub())
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = _cfg.P2PProtocolVersion,
                AgentVersion = ProductInfo.ClientId
            })
            .AddSingleton(new PubsubSettings()
            {
                ReconnectionAttempts = int.MaxValue,
                Degree = 3,
                LowestDegree = 2,
                HighestDegree = 6,
                LazyDegree = 3
            });

        if (_cfg.P2PLogsEnabled)
        {
            serviceCollection
                .AddSingleton<ILoggerFactory>(new NethermindLoggerFactory(logManager))
                .AddLogging(builder =>
                    builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace)
                    .AddSimpleConsole(l =>
                    {
                        l.SingleLine = true;
                        l.TimestampFormat = "[HH:mm:ss.FFF]";
                    })
                );
        }
        _serviceProvider = serviceCollection.BuildServiceProvider();

        IPeerFactory peerFactory = _serviceProvider!.GetService<IPeerFactory>()!;

        Identity identity = GetPeerIdentity(fileSystem, _cfg, keyStoreConfig);
        _peer = peerFactory.Create(identity);
        _router = _serviceProvider!.GetService<PubsubRouter>()!;
        _disc = new(_router, _peerStore = _serviceProvider.GetService<PeerStore>()!, new PubsubPeerDiscoverySettings() { Interval = 300 }, _peer);
        ITopic topic = _router.GetTopic("decryptionKeys");

        topic.OnMessage += (byte[] msg) =>
        {
            _msgQueue.Writer.TryWrite(msg);
            if (_logger.IsTrace) _logger.Trace("Received Shutter P2P message.");
        };
    }

    public async Task Start(IEnumerable<Multiaddress> bootnodeP2PAddresses, Func<Dto.DecryptionKeys, Task> onKeysReceived, CancellationToken cancellationToken)
    {
        await _peer.StartListenAsync([_address], cancellationToken);
        await _router.StartAsync(_peer, cancellationToken);
        _ = _disc.StartDiscoveryAsync([Multiaddress.Decode(_address)], cancellationToken);

        foreach (Multiaddress address in bootnodeP2PAddresses)
        {
            _peerStore.Discover([address]);
        }

        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {_address}");

        long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
        bool hasTimedOut = false;

        while (true)
        {
            try
            {
                using var timeoutSource = new CancellationTokenSource(hasTimedOut ? DisconnectionLogInterval : DisconnectionLogTimeout);
                using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

                byte[] msg = await _msgQueue.Reader.ReadAsync(source.Token);
                lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                hasTimedOut = false;
                ProcessP2PMessage(msg, onKeysReceived);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Shutting down Shutter P2P...");
                    break;
                }
                else if (_logger.IsWarn)
                {
                    hasTimedOut = true;
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
    }

    public async ValueTask DisposeAsync()
    {
        _router?.UnsubscribeAll();
        await (_serviceProvider?.DisposeAsync() ?? default);
    }

    private Identity GetPeerIdentity(IFileSystem fileSystem, IShutterConfig shutterConfig, IKeyStoreConfig keyStoreConfig)
    {
        string fp = shutterConfig.ShutterKeyFile.GetApplicationResourcePath(keyStoreConfig.KeyStoreDirectory);
        Identity identity;

        if (fileSystem.File.Exists(fp))
        {
            if (_logger.IsInfo) _logger.Info("Loading Shutter P2P identity from disk.");
            identity = new(fileSystem.File.ReadAllBytes(fp));
        }
        else
        {
            if (_logger.IsInfo) _logger.Info("Generating new Shutter P2P identity.");
            identity = new();
            string keyStoreDirectory = keyStoreConfig.KeyStoreDirectory.GetApplicationResourcePath();
            fileSystem.Directory.CreateDirectory(keyStoreDirectory);
            fileSystem.File.WriteAllBytes(fp, identity.PrivateKey!.Data.ToByteArray());
        }

        return identity;
    }

    private void ProcessP2PMessage(byte[] msg, Func<Dto.DecryptionKeys, Task> onKeysReceived)
    {
        if (_logger.IsTrace) _logger.Trace("Processing Shutter P2P message.");

        try
        {
            Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
            if (envelope.Message.TryUnpack(out Dto.DecryptionKeys decryptionKeys))
            {
                _ = onKeysReceived(decryptionKeys);
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug($"Could not parse Shutter decryption keys: protobuf type names did not match.");
            }
        }
        catch (InvalidProtocolBufferException e)
        {
            if (_logger.IsDebug) _logger.Warn($"DEBUG/ERROR Could not parse Shutter decryption keys: {e}");
        }
    }
}
