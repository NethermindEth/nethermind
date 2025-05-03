// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;
using Nethermind.Merge.Plugin.Data;
using Snappier;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;
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
    private readonly ICLConfig _config;
    private readonly string _blocksV2TopicId;
    private readonly Channel<ExecutionPayloadV3> _blocksP2PMessageChannel = Channel.CreateBounded<ExecutionPayloadV3>(10); // for safety add capacity

    private PubsubRouter? _router;
    private ILocalPeer? _localPeer;
    private ITopic? _blocksV2Topic;
    private PeerStore? _peerStore;

    private ulong? _headNumber = null;

    public OptimismCLP2P(
        ulong chainId,
        string[] staticPeerList,
        ICLConfig config,
        Address sequencerP2PAddress,
        ITimestamper timestamper,
        ILogManager logManager,
        IExecutionEngineManager executionEngineManager)
    {
        _logger = logManager.GetClassLogger();
        _config = config;
        _executionEngineManager = executionEngineManager;
        _staticPeerList = staticPeerList.Select(Multiaddress.Decode).ToArray();
        _blockValidator = new P2PBlockValidator(chainId, sequencerP2PAddress, timestamper, _logger);

        _blocksV2TopicId = $"/optimism/{chainId}/2/blocks";

        // Jagger

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
                    return;
                }

                ExecutionPayloadV3? requestedPayload = await RequestPayload((ulong)payload.BlockNumber, token);
                _logger.Error($"Requested payload: {requestedPayload?.BlockHash}");

                // ulong numberOfMissingBlocks = _headNumber!.Value - (ulong)payload.BlockNumber;

                // if (numberOfMissingBlocks > 0)
                // {
                //
                // }

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

    private async Task<ExecutionPayloadV3?> RequestPayload(ulong payloadNumber, CancellationToken token)
    {
        _logger.Error($"Calling peer {_staticPeerList[13]}");
        ISession? remotePeer = null;
        try
        {
            remotePeer = await _localPeer!.DialAsync(_staticPeerList[13], token);
            _logger.Error(
                $"Done {string.Join(" ", _peerStore!.GetPeerInfo(remotePeer.RemoteAddress.GetPeerId()!).SupportedProtocols ?? [])}");
            ExecutionPayloadV3? response =
                await remotePeer.DialAsync<PayloadByNumberProtocol, ulong, ExecutionPayloadV3?>(payloadNumber, token);
            _logger.Error($"Response: {response?.BlockHash}");
            return response;
        }
        finally
        {
            if (remotePeer is not null) await remotePeer.DisconnectAsync();
        }
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

    public async Task Run(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Starting Optimism CL P2P");

        IPeerFactory peerFactory = _serviceProvider.GetService<IPeerFactory>()!;
        string address = $"/ip4/{_config.P2PHost}/tcp/{_config.P2PPort}";
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
                var supportedProtocols = _peerStore.GetPeerInfo(multiaddress.GetPeerId()!).SupportedProtocols;
                _logger.Error($"Supported protocols: {string.Join(" ", supportedProtocols ?? [])}");
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

    public void Dispose()
    {
        _blocksV2Topic?.Unsubscribe();
        _blocksP2PMessageChannel.Writer.Complete();
    }
}
