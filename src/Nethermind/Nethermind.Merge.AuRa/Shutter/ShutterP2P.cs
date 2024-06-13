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
using ILogger = Nethermind.Logging.ILogger;
using System.Threading.Channels;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P
{
    private readonly Action<Dto.DecryptionKeys> _onDecryptionKeysReceived;
    private readonly ILogger _logger;
    private readonly Channel<byte[]> _msgQueue = Channel.CreateUnbounded<byte[]>();
    private readonly IAuraConfig _auraConfig;
    private PubsubRouter? _router;
    private ServiceProvider? _serviceProvider;
    private CancellationTokenSource? _cancellationTokenSource;

    public ShutterP2P(Action<Dto.DecryptionKeys> onDecryptionKeysReceived, IAuraConfig auraConfig, ILogManager logManager)
    {
        _onDecryptionKeysReceived = onDecryptionKeysReceived;
        _logger = logManager.GetClassLogger();
        _auraConfig = auraConfig;
    }

    public void Start(in IEnumerable<string> p2pAddresses)
    {
        _serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = _auraConfig.ShutterP2PProtocolVersion,
                AgentVersion = _auraConfig.ShutterP2PAgentVersion
            })
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
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + _auraConfig.ShutterP2PPort);
        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {peer.Address}");
        _router = _serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = _router.Subscribe("decryptionKeys");

        topic.OnMessage += (byte[] msg) =>
        {
            _msgQueue.Writer.TryWrite(msg);
            if (_logger.IsDebug) _logger.Debug($"Received Shutter P2P message.");
        };

        MyProto proto = new();
        _cancellationTokenSource = new();
        _ = _router.RunAsync(peer, proto, token: _cancellationTokenSource.Token);
        ConnectToPeers(proto, p2pAddresses);

        long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
        long delta = 0;

        Task.Run(() =>
        {
            for (; ; )
            {
                Thread.Sleep(250);

                while (_msgQueue.Reader.TryRead(out var msg))
                {
                    ProcessP2PMessage(msg);
                    lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                }

                long oldDelta = delta;
                delta = DateTimeOffset.Now.ToUnixTimeSeconds() - lastMessageProcessed;

                if (delta > 0 && delta % (60 * 5) == 0 && delta != oldDelta)
                {
                    if (_logger.IsWarn) _logger.Warn($"Not receiving Shutter messages ({delta / 60}m)...");
                }
            }
        }, _cancellationTokenSource.Token);
    }

    public void DisposeAsync()
    {
        _router?.UnsubscribeAll();
        _ = _serviceProvider?.DisposeAsync();
        _cancellationTokenSource?.Cancel();
    }

    internal class MyProto : IDiscoveryProtocol
    {
        public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
        public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

        public Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
        {
            return Task.Delay(int.MaxValue);
        }
    }

    internal void ProcessP2PMessage(byte[] msg)
    {
        if (_logger.IsDebug) _logger.Debug($"Processing Shutter P2P message.");

        Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
        if (!envelope.Message.TryUnpack(out Dto.DecryptionKeys decryptionKeys))
        {
            if (_logger.IsDebug) _logger.Debug("Could not parse Shutter decryption keys...");
            return;
        }

        _onDecryptionKeysReceived(decryptionKeys);
    }

    internal void ConnectToPeers(MyProto proto, IEnumerable<string> p2pAddresses)
    {
        foreach (string addr in p2pAddresses)
        {
            proto.OnAddPeer?.Invoke([addr]);
        }
    }
}
