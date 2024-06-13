using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Multiformats.Address;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P
{
    private readonly Action<Dto.DecryptionKeys> _onDecryptionKeysReceived;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<byte[]> _msgQueue = new();
    private PubsubRouter _router;
    private ILocalPeer _peer;

    public ShutterP2P(Action<Dto.DecryptionKeys> onDecryptionKeysReceived, IAuraConfig auraConfig, ILogManager logManager)
    {
        _onDecryptionKeysReceived = onDecryptionKeysReceived;
        _logger = logManager.GetClassLogger();

        ServiceProvider serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = auraConfig.ShutterP2PProtocolVersion,
                AgentVersion = auraConfig.ShutterP2PAgentVersion
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

        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
        _peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + auraConfig.ShutterP2PPort);
        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {_peer.Address}");
        _router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = _router.Subscribe("decryptionKeys");

        topic.OnMessage += (byte[] msg) =>
        {
            _msgQueue.Enqueue(msg);
            if (_logger.IsDebug) _logger.Debug($"Received Shutter P2P message.");
        };
    }

    public void Start(in IEnumerable<string> p2pAddresses)
    {
        MyProto proto = new();
        CancellationTokenSource ts = new();
        _ = _router.RunAsync(_peer, proto, token: ts.Token);
        ConnectToPeers(proto, p2pAddresses);

        long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
        long delta = 0;

        Task.Run(() =>
        {
            for (; ; )
            {
                Thread.Sleep(250);

                while (_msgQueue.TryDequeue(out var msg))
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
        });

        // todo: use cancellation source on finish
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
