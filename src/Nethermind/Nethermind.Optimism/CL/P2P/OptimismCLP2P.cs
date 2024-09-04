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

    public OptimismCLP2P(ulong chainId, ITimestamper timestamper, ILogManager logManager, IOptimismEngineRpcModule engineRpcModule)
    {
        _logger = logManager.GetClassLogger();
        _engineRpcModule = engineRpcModule;
        _payloadDecoder = new PayloadDecoder();
        _blockValidator = new P2PBlockValidator(chainId, timestamper, _logger);
    }

    public void Start()
    {
        _logger.Error("Starting p2p");
        _serviceProvider = new ServiceCollection()
            // .AddLogging(builder =>
            //     builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace)
            //     .AddSimpleConsole(l =>
            //     {
            //         l.SingleLine = true;
            //         l.TimestampFormat = "[HH:mm:ss.FFF]";
            //     })
            // )
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = "",
                AgentVersion = "optimism"
            })
            .AddSingleton(new Settings()
            {

            })
            .BuildServiceProvider();

        IPeerFactory peerFactory = _serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/3030");
        if (_logger.IsInfo) _logger.Info($"Started P2P: {peer.Address}");

        _router = _serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = _router.Subscribe("/optimism/11155420/2/blocks");
        topic.OnMessage += OnMessage;

        MyProto proto = new();
        _cancellationTokenSource = new();
        _ = _router.RunAsync(peer, proto, new Settings
        {
            DefaultSignaturePolicy = Settings.SignaturePolicy.StrictNoSign,
            GetMessageId = (message => CalculateMessageId(message))
        }, token: _cancellationTokenSource.Token);
        // proto.OnAddPeer?.Invoke(["/ip4/5.9.87.214/tcp/9222/p2p/16Uiu2HAm39UNArTiqPNakHqA58Rss4Fz8f41otDj4sWKbaA7BazG"]);
        proto.OnAddPeer?.Invoke(["/ip4/217.22.153.164/tcp/31660/p2p/16Uiu2HAmG5hBYavoanawCzz1cu5H7XNNSaA7BYNvwa7DNmojei6g"]);
    }

    void OnMessage(byte[] msg)
    {
        int length = Snappy.GetUncompressedLength(msg);
        byte[] decompressed = new byte[length];
        Snappy.Decompress(msg, decompressed);

        byte[] signature = decompressed[0..65];
        byte[] payloadData = decompressed[65..];

        // if (_logger.IsError)
        //     _logger.Error($"Signature {BitConverter.ToString(signature).Replace("-", string.Empty)}");

        var payloadDecoded = _payloadDecoder.DecodePayload(payloadData);

        if (payloadDecoded.TryGetBlock(out Block? block))
        {
            if (_logger.IsError)
            {
                _logger.Error($"HASH {block!.Header.CalculateHash()}");
                // _logger.Error($"GOT BLOCK {block!.Header.ToString(BlockHeader.Format.Full)}");
            }
        }

        var validationResult = _blockValidator.Validate(payloadDecoded, payloadData, signature, P2PTopic.BlocksV3);

        _logger.Error($"VALIDATION RESULT {validationResult}");

        if (validationResult == ValidityStatus.Reject)
        {
            // TODO decrease peers rating
            return;
        }

        if (validationResult == ValidityStatus.Ignore)
        {
            return;
        }

        // await Task.Delay(5000);

        // var npResult = await _engineRpcModule.engine_newPayloadV3(payloadDecoded, Array.Empty<byte[]>(),
        //     payloadDecoded.ParentBeaconBlockRoot);
        //
        // _logger.Error($"NP RESULT {npResult.Data.Status}");
        //
        // var fcuResult = await _engineRpcModule.engine_forkchoiceUpdatedV3(
        //     new ForkchoiceStateV1(payloadDecoded.BlockHash, payloadDecoded.BlockHash, payloadDecoded.BlockHash), null);
        //
        // _logger.Error($"FCU RESULT {fcuResult.Data.PayloadStatus.Status}");
    }

    private MessageId CalculateMessageId(Message message)
    {
        // TODO: create proper function
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha256.AppendData(BitConverter.GetBytes((ulong)message.Topic.Length));
        sha256.AppendData(Encoding.ASCII.GetBytes(message.Topic));
        sha256.AppendData(message.Data.Span);
        return new MessageId(sha256.GetHashAndReset());
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
}
