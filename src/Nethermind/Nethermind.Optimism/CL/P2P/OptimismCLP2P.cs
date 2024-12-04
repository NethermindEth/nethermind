// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core;
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
    private readonly string[] _staticPeerList;

    private readonly string _blocksV2TopicId;

    private ITopic? _blocksV2Topic;

    public OptimismCLP2P(ulong chainId, string[] staticPeerList, byte[] sequencerPubkey, ITimestamper timestamper, ILogManager logManager, IOptimismEngineRpcModule engineRpcModule)
    {
        _logger = logManager.GetClassLogger();
        _staticPeerList = staticPeerList;
        _engineRpcModule = engineRpcModule;
        _payloadDecoder = new PayloadDecoder();
        _blockValidator = new P2PBlockValidator(chainId, sequencerPubkey, timestamper, _logger);

        _blocksV2TopicId = $"/optimism/{chainId}/2/blocks";
    }

    public void Start()
    {
        if (_logger.IsInfo) _logger.Info("Starting Optimism CL p2p");
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

        _blocksV2Topic = _router.GetTopic(_blocksV2TopicId);
        _blocksV2Topic.OnMessage += OnMessage;


        _cancellationTokenSource = new();
        _ = _router.RunAsync(peer, new Settings
        {
            DefaultSignaturePolicy = Settings.SignaturePolicy.StrictNoSign,
            GetMessageId = (message => CalculateMessageId(message))
        }, token: _cancellationTokenSource.Token);

        PeerStore peerStore = _serviceProvider.GetService<PeerStore>()!;
        foreach (string peerAddress in _staticPeerList)
        {
            try
            {
                peerStore.Discover([peerAddress]);
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Unable to discover peer({peerAddress}). Error: {e.Message}");
            }
        }

        if (_logger.IsInfo) _logger.Info($"Started P2P: {peer.Address}");
    }

    async void OnMessage(byte[] msg)
    {
        // TODO: handle missed payloads
        int length = Snappy.GetUncompressedLength(msg);
        _logger.Info($"Received length: {length}");
        if (length < 65)
        {
            // TODO: decrease peers rating
            return;
        }
        byte[] decompressed = new byte[length];
        Snappy.Decompress(msg, decompressed);

        byte[] signature = decompressed[0..65];
        byte[] payloadData = decompressed[65..];

        if (_blockValidator.ValidateSignature(signature, payloadData) != ValidityStatus.Valid)
        {
            // TODO: decrease peers rating
            return;
        }

        ExecutionPayloadV3 payloadDecoded;
        try
        {
            payloadDecoded = _payloadDecoder.DecodePayload(payloadData);
        }
        catch (ArgumentException e)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Unable to decode payload from p2p. {e.Message}");
            }
            // TODO: decrease peers rating
            return;
        }

        if (_logger.IsInfo)
        {
            _logger.Info($"Got block from CL P2P: {payloadDecoded.BlockHash}");
        }

        var validationResult = _blockValidator.Validate(payloadDecoded, P2PTopic.BlocksV3);

        if (validationResult == ValidityStatus.Reject)
        {
            // TODO: decrease peers rating
            return;
        }

        var npResult = await _engineRpcModule.engine_newPayloadV3(payloadDecoded, Array.Empty<byte[]>(),
            payloadDecoded.ParentBeaconBlockRoot);

        if (npResult.Result.ResultType == ResultType.Failure)
        {
            if (_logger.IsError)
            {
                _logger.Error($"NewPayload request error: {npResult.Result.Error}");
            }
            return;
        }

        if (npResult.Data.Status == PayloadStatus.Invalid)
        {
            if (_logger.IsTrace) _logger.Trace($"Got invalid payload from p2p");
            return;
        }

        var fcuResult = await _engineRpcModule.engine_forkchoiceUpdatedV3(
            new ForkchoiceStateV1(payloadDecoded.BlockHash, payloadDecoded.BlockHash, payloadDecoded.BlockHash),
            null);

        if (fcuResult.Result.ResultType == ResultType.Failure)
        {
            if (_logger.IsError)
            {
                _logger.Error($"ForkChoiceUpdated request error: {npResult.Result.Error}");
            }
            return;
        }

        if (fcuResult.Data.PayloadStatus.Status == PayloadStatus.Invalid)
        {
            if (_logger.IsTrace) _logger.Trace($"Got invalid payload from p2p");
        }
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
