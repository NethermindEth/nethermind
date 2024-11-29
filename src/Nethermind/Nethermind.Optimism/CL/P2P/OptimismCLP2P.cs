// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.Rpc;
using Snappier;

namespace Nethermind.Optimism;

public class OptimismCLP2P
{
    private ServiceProvider? _serviceProvider;
    private CancellationTokenSource? _cancellationTokenSource;
    private PubsubRouter? _router;
    private readonly ILogger _logger;

    private readonly IOptimismEngineRpcModule _engineRpcModule;
    private readonly IPayloadDecoder _payloadDecoder;
    private readonly IP2PBlockValidator _blockValidator;
    private readonly ulong _chainId;

    public OptimismCLP2P(ulong chainId, byte[] sequencerPubkey, ITimestamper timestamper, ILogManager logManager, IOptimismEngineRpcModule engineRpcModule)
    {
        _chainId = chainId;
        _logger = logManager.GetClassLogger();
        _engineRpcModule = engineRpcModule;
        _payloadDecoder = new PayloadDecoder();
        _blockValidator = new P2PBlockValidator(chainId, sequencerPubkey, timestamper, _logger);
    }

    public void Start()
    {
        _logger.Error("Starting p2p");
        _serviceProvider = new ServiceCollection()
            .AddSingleton<PeerStore>()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = "",
                AgentVersion = "optimism"
            })
            .AddSingleton(new Settings())
            .BuildServiceProvider();

        IPeerFactory peerFactory = _serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/3030");

        _router = _serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = _router.GetTopic($"/optimism/{_chainId}/2/blocks");
        topic.OnMessage += OnMessage;

        _cancellationTokenSource = new();
        _ = _router.RunAsync(peer, new Settings
        {
            DefaultSignaturePolicy = Settings.SignaturePolicy.StrictNoSign,
            GetMessageId = (message => CalculateMessageId(message))
        }, token: _cancellationTokenSource.Token);

        PeerStore peerStore = _serviceProvider.GetService<PeerStore>()!;
        peerStore.Discover(["/ip4/5.9.87.214/tcp/9222/p2p/16Uiu2HAm39UNArTiqPNakHqA58Rss4Fz8f41otDj4sWKbaA7BazG"]);
        peerStore.Discover(["/ip4/217.22.153.164/tcp/31660/p2p/16Uiu2HAmG5hBYavoanawCzz1cu5H7XNNSaA7BYNvwa7DNmojei6g"]);

        if (_logger.IsInfo) _logger.Info($"Started P2P: {peer.Address}");
    }

    async void OnMessage(byte[] msg)
    {
        int length = Snappy.GetUncompressedLength(msg);
        byte[] decompressed = new byte[length];
        Snappy.Decompress(msg, decompressed);

        byte[] signature = decompressed[0..65];
        byte[] payloadData = decompressed[65..];

        var payloadDecoded = _payloadDecoder.DecodePayload(payloadData);
        var validationResult = _blockValidator.Validate(payloadDecoded, payloadData, signature, P2PTopic.BlocksV3);

        if (validationResult == ValidityStatus.Reject)
        {
            // TODO decrease peers rating
            return;
        }

        if (_logger.IsInfo)
        {
            _logger.Info($"Got block from CL P2P: {payloadDecoded.BlockHash}");
        }

        var npResult = await _engineRpcModule.engine_newPayloadV3(payloadDecoded, Array.Empty<byte[]>(),
            payloadDecoded.ParentBeaconBlockRoot);

        var fcuResult = await _engineRpcModule.engine_forkchoiceUpdatedV3(
            new ForkchoiceStateV1(payloadDecoded.BlockHash, payloadDecoded.BlockHash, payloadDecoded.BlockHash), null);
    }

    private MessageId CalculateMessageId(Message message)
    {
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha256.AppendData(BitConverter.GetBytes((ulong)message.Topic.Length));
        sha256.AppendData(Encoding.ASCII.GetBytes(message.Topic));
        sha256.AppendData(message.Data.Span);
        return new MessageId(sha256.GetHashAndReset());
    }
}
