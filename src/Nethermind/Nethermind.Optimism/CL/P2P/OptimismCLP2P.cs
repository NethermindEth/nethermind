// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.Rpc;
using Nethermind.Core.Crypto;
using Snappier;

namespace Nethermind.Optimism;

public class OptimismCLP2P : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private PubsubRouter? _router;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ILogger _logger;
    private readonly IOptimismEngineRpcModule _engineRpcModule;
    private readonly IP2PBlockValidator _blockValidator;
    private readonly Multiaddress[] _staticPeerList;
    private readonly ICLConfig _config;
    private ILocalPeer? _localPeer;

    private readonly string _blocksV2TopicId;

    private ITopic? _blocksV2Topic;

    public OptimismCLP2P(ulong chainId, string[] staticPeerList, ICLConfig config, Address sequencerP2PAddress, ITimestamper timestamper, ILogManager logManager, IOptimismEngineRpcModule engineRpcModule)
    {
        _logger = logManager.GetClassLogger();
        _config = config;
        _staticPeerList = staticPeerList.Select(addr => Multiaddress.Decode(addr)).ToArray();
        _engineRpcModule = engineRpcModule;
        _blockValidator = new P2PBlockValidator(chainId, sequencerP2PAddress, timestamper, _logger);

        _blocksV2TopicId = $"/optimism/{chainId}/2/blocks";

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
    }

    private ulong _headPayloadNumber = 0;
    private readonly SemaphoreSlim _semaphore = new(1);
    async void OnMessage(byte[] msg)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (TryValidateAndDecodePayload(msg, out var payload))
            {
                if (_logger.IsTrace) _logger.Trace($"Received payload prom p2p: {payload}");

                if (_headPayloadNumber >= (ulong)payload.BlockNumber)
                {
                    // Old payload. skip
                    return;
                }

                if (await SendNewPayloadToEL(payload) && await SendForkChoiceUpdatedToEL(payload.BlockHash))
                {
                    _headPayloadNumber = (ulong)payload.BlockNumber;
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private bool TryValidateAndDecodePayload(byte[] msg, [MaybeNullWhen(false)] out ExecutionPayloadV3 payload)
    {
        int length = Snappy.GetUncompressedLength(msg);
        if (length < 65)
        {
            payload = null;
            return false;
        }

        byte[] decompressed = new byte[length];
        Snappy.Decompress(msg, decompressed);

        byte[] signature = decompressed[0..65];
        byte[] payloadData = decompressed[65..];

        if (_blockValidator.ValidateSignature(payloadData, signature) != ValidityStatus.Valid)
        {
            payload = null;
            return false;
        }

        try
        {
            payload = PayloadDecoder.Instance.DecodePayload(payloadData);
        }
        catch (ArgumentException e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to decode payload from p2p. {e.Message}");

            payload = null;
            return false;
        }

        var validationResult = _blockValidator.Validate(payload, P2PTopic.BlocksV3);

        if (validationResult == ValidityStatus.Reject)
        {
            payload = null;
            return false;
        }
        return true;
    }

    private async Task<bool> SendNewPayloadToEL(ExecutionPayloadV3 executionPayload)
    {
        var npResult = await _engineRpcModule.engine_newPayloadV3(executionPayload, Array.Empty<byte[]>(),
            executionPayload.ParentBeaconBlockRoot);

        if (npResult.Result.ResultType == ResultType.Failure)
        {
            if (_logger.IsError)
            {
                _logger.Error($"NewPayload request error: {npResult.Result.Error}");
            }
            return false;
        }

        if (npResult.Data.Status == PayloadStatus.Invalid)
        {
            if (_logger.IsTrace) _logger.Trace($"Got invalid payload from p2p");
            return false;
        }

        return true;
    }

    private async Task<bool> SendForkChoiceUpdatedToEL(Hash256 headBlockHash)
    {
        var fcuResult = await _engineRpcModule.engine_forkchoiceUpdatedV3(
            new ForkchoiceStateV1(headBlockHash, headBlockHash, headBlockHash),
            null);

        if (fcuResult.Result.ResultType == ResultType.Failure)
        {
            if (_logger.IsError)
            {
                _logger.Error($"ForkChoiceUpdated request error: {fcuResult.Result.Error}");
            }
            return false;
        }

        if (fcuResult.Data.PayloadStatus.Status == PayloadStatus.Invalid)
        {
            if (_logger.IsTrace) _logger.Trace($"Got invalid payload from p2p");
            return false;
        }

        return true;
    }

    public void Start()
    {
        if (_logger.IsInfo) _logger.Info("Starting Optimism CL P2P");

        IPeerFactory peerFactory = _serviceProvider.GetService<IPeerFactory>()!;
        _localPeer = peerFactory.Create(new Identity(), $"/ip4/{_config.P2PHost}/tcp/{_config.P2PPort}");

        _router = _serviceProvider.GetService<PubsubRouter>()!;

        _blocksV2Topic = _router.GetTopic(_blocksV2TopicId);
        _blocksV2Topic.OnMessage += OnMessage;

        _ = _router.RunAsync(_localPeer, new Settings
        {
            DefaultSignaturePolicy = Settings.SignaturePolicy.StrictNoSign,
            GetMessageId = (message => CalculateMessageId(message))
        }, token: _cancellationTokenSource.Token);

        PeerStore peerStore = _serviceProvider.GetService<PeerStore>()!;
        peerStore.Discover(_staticPeerList);

        if (_logger.IsInfo) _logger.Info($"Started P2P: {_localPeer.Address}");
    }

    private MessageId CalculateMessageId(Message message)
    {
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha256.AppendData(BitConverter.GetBytes((ulong)message.Topic.Length));
        sha256.AppendData(Encoding.ASCII.GetBytes(message.Topic));
        sha256.AppendData(message.Data.Span);
        return new MessageId(sha256.GetHashAndReset());
    }

    public void Dispose()
    {
        _blocksV2Topic?.Unsubscribe();
        _cancellationTokenSource.Cancel();
    }
}
