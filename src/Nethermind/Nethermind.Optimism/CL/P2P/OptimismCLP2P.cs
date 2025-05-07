// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;
using Nethermind.Merge.Plugin.Data;
using Snappier;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core.Discovery;
using Channel = System.Threading.Channels.Channel;

namespace Nethermind.Optimism.CL.P2P;

public class OptimismCLP2P : IDisposable
{
    private const int MaxGossipSize = 10485760;

    private readonly ServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private readonly IExecutionEngineManager _executionEngineManager;
    private readonly P2PBlockValidator _blockValidator;
    private readonly Multiaddress[] _staticPeerList;
    private readonly IOptimismConfig _config;
    private readonly string _blocksV2TopicId;
    private readonly Channel<ExecutionPayloadV3> _blocksP2PMessageChannel = Channel.CreateBounded<ExecutionPayloadV3>(10); // for safety add capacity
    private readonly IPeerManager _peerManager;
    private readonly IPAddress _externalIp;

    private PubsubRouter? _router;
    private ILocalPeer? _localPeer;
    private ITopic? _blocksV2Topic;
    private PeerStore? _peerStore;

    private ulong? _headNumber = null;

    public OptimismCLP2P(
        ulong chainId,
        string[] staticPeerList,
        IOptimismConfig config,
        Address sequencerP2PAddress,
        ITimestamper timestamper,
        IPAddress externalIp,
        ILogManager logManager,
        IExecutionEngineManager executionEngineManager)
    {
        _logger = logManager.GetClassLogger();
        _config = config;
        _executionEngineManager = executionEngineManager;
        _staticPeerList = staticPeerList.Select(Multiaddress.Decode).ToArray();
        _blockValidator = new P2PBlockValidator(chainId, sequencerP2PAddress, timestamper, _logger);
        _peerManager = new PeerManager(_logger);
        _externalIp = externalIp;

        _blocksV2TopicId = $"/optimism/{chainId}/2/blocks";

        _serviceProvider = new ServiceCollection()
            .AddSingleton<PeerStore>()
            .AddSingleton(new PayloadByNumberProtocol(chainId, PayloadDecoder.Instance, _logger))
            .AddLibp2p(builder => builder.WithPubsub().AddAppLayerProtocol<PayloadByNumberProtocol>())
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = "",
                AgentVersion = "optimism"
            })
            .AddSingleton(new PubsubSettings()
            {
                ReconnectionAttempts = int.MaxValue,
                Degree = 3,
                LowestDegree = 2,
                HighestDegree = 6,
                LazyDegree = 3,
                DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
                GetMessageId = CalculateMessageId
            })
            .BuildServiceProvider();
    }

    private async void OnMessage(byte[] msg, CancellationToken token)
    {
        try
        {
            if (TryValidateAndDecodePayload(msg, out var payload))
            {
                if (_logger.IsTrace) _logger.Trace($"Received payload prom p2p: {payload}");
                if ((ulong)payload.BlockNumber <= _headNumber)
                {
                    return;
                }
                await _blocksP2PMessageChannel.Writer.WriteAsync(payload, token);
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn("Got invalid message from P2P");
            }
        }
        catch (Exception e)
        {
            if (e is not OperationCanceledException && _logger.IsError) _logger.Error("Unhandled exception in Optimism CL P2P:", e);
        }
    }

    private async Task MainLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                ExecutionPayloadV3 payload = await _blocksP2PMessageChannel.Reader.ReadAsync(token);

                if ((ulong)payload.BlockNumber <= _headNumber)
                {
                    // Old payload. skip
                    return;
                }

                if (_headNumber is not null)
                {
                    ulong numberOfMissingPayloads = (ulong)payload.BlockNumber - _headNumber.Value - 1;
                    ExecutionPayloadV3?[] missingPayloads = new ExecutionPayloadV3[numberOfMissingPayloads];
                    Hash256 previousParentHash = payload.ParentHash;
                    // Rollback missing payloads
                    for (ulong i = numberOfMissingPayloads; i > 0; i--)
                    {
                        ulong payloadNumber = _headNumber.Value + i;
                        ExecutionPayloadV3? missingPayload = await RequestPayload(payloadNumber, previousParentHash, token);
                        if (missingPayload is null)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Unable to request missing payload. Number: {payloadNumber}");
                            break;
                        }
                        missingPayloads[i - 1] = missingPayload;
                    }

                    foreach (var missingPayload in missingPayloads)
                    {
                        if (missingPayload is not null && await _executionEngineManager.ProcessNewP2PExecutionPayload(missingPayload))
                        {
                            _headNumber = (ulong)missingPayload.BlockNumber;
                        }
                    }
                }

                if (_blockValidator.IsBlockNumberPerHeightLimitReached(payload) is not ValidityStatus.Valid)
                {
                    return;
                }

                if (await _executionEngineManager.ProcessNewP2PExecutionPayload(payload))
                {
                    _headNumber = (ulong)payload.BlockNumber;
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError && e is not OperationCanceledException and not ChannelClosedException)
                    _logger.Error("Unhandled exception in Optimism CL P2P:", e);
            }
        }
    }

    private async Task<ExecutionPayloadV3?> TryRequestPayload
        (ISession? session, ulong payloadNumber, Hash256 expectedHash, CancellationToken token)
    {
        try
        {
            using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(5));
            if (session is null) return null;
            ExecutionPayloadV3? payload =
                await session.DialAsync<PayloadByNumberProtocol, ulong, ExecutionPayloadV3?>(payloadNumber, cancellationTokenSource.Token);
            if (payload is null || payload.BlockHash != expectedHash) return null;
            return payload;
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to request payload {payloadNumber} ({expectedHash}). Exception: {e}");
            return null;
        }
    }

    private async Task<ExecutionPayloadV3?> RequestPayload(ulong payloadNumber, Hash256 expectedHash, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Requesting missing payload. Number: {payloadNumber}, Expected hash: {expectedHash}");
        try
        {
            ExecutionPayloadV3? response = null;
            foreach (Multiaddress peer in _peerManager.GetPeers())
            {
                ISession? remotePeer = await TryDialPeer(peer, token);
                response = await TryRequestPayload(remotePeer, payloadNumber, expectedHash, token);
                if (response is not null)
                {
                    _peerManager.IncreaseRating(peer);
                    await remotePeer!.DisconnectAsync();
                    break;
                }

                if (remotePeer is not null) await remotePeer.DisconnectAsync();
                _peerManager.DecreaseRating(peer);
            }

            if (response is null)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Unable to request payload. Number: {payloadNumber}, Expected hash: {expectedHash}");
                return null;
            }

            return response;
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to request payload. Number: {payloadNumber}, Expected hash: {expectedHash}. Exception: {e}");
        }

        return null;
    }

    private bool TryValidateAndDecodePayload(byte[] msg, [MaybeNullWhen(false)] out ExecutionPayloadV3 payload)
    {
        int length = Snappy.GetUncompressedLength(msg);
        if (length is < 65 or > MaxGossipSize)
        {
            payload = null;
            return false;
        }

        using ArrayPoolList<byte> decompressed = new(length, length);
        Snappy.Decompress(msg, decompressed.AsSpan());

        Span<byte> signature = decompressed.AsSpan()[..65];
        ReadOnlySpan<byte> payloadData = decompressed.AsSpan()[65..];

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

        ValidityStatus validationResult = _blockValidator.Validate(payload, P2PTopic.BlocksV3);

        if (validationResult == ValidityStatus.Reject)
        {
            payload = null;
            return false;
        }
        return true;
    }

    private async Task<ISession?> TryDialPeer(Multiaddress remotePeer, CancellationToken token)
    {
        try
        {
            using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(5));
            return await _localPeer!.DialAsync(remotePeer, cancellationTokenSource.Token);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to Dial peer {remotePeer}. Exception: {e.Message}");
            return null;
        }
    }

    public async Task Run(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Starting Optimism CL P2P");

        IPeerFactory peerFactory = _serviceProvider.GetService<IPeerFactory>()!;
        string hostIp = _config.ClP2PHost ?? _externalIp.ToString();
        string address = $"/ip4/{hostIp}/tcp/{_config.ClP2PPort}";
        _localPeer = peerFactory.Create(new Identity());

        _router = _serviceProvider.GetService<PubsubRouter>()!;
        _blocksV2Topic = _router.GetTopic(_blocksV2TopicId);
        _blocksV2Topic.OnMessage += msg => OnMessage(msg, token);
        try
        {
            await _localPeer.StartListenAsync([address], token);
            await _router.StartAsync(_localPeer, token);

            _peerStore = _serviceProvider.GetService<PeerStore>()!;
            foreach (var multiaddress in _staticPeerList)
            {
                _peerStore.Discover([multiaddress]);

                ISession? session = await TryDialPeer(multiaddress, token);
                if (session is not null)
                {
                    if (_logger.IsInfo) _logger.Info($"Adding active peer {multiaddress}");
                    _peerManager.AddActivePeer(multiaddress);
                }
                else
                {
                    _peerManager.AddInactivePeer(multiaddress);
                }

                if (session is not null) await session.DisconnectAsync();
            }
        }
        catch (Exception e)
        {
            _logger.Error($"{e}");
        }


        if (_logger.IsInfo) _logger.Info($"Started P2P: {address}");
        await MainLoop(token);
    }

    private MessageId CalculateMessageId(Message message)
    {
        var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha256.AppendData(BitConverter.GetBytes((ulong)message.Topic.Length));
        sha256.AppendData(Encoding.ASCII.GetBytes(message.Topic));
        sha256.AppendData(message.Data.Span);
        return new MessageId(sha256.GetHashAndReset());
    }

    public void Reset(ulong headNumber)
    {
        _headNumber = headNumber;
    }

    public void Dispose()
    {
        _blocksV2Topic?.Unsubscribe();
        _blocksP2PMessageChannel.Writer.Complete();
    }
}
