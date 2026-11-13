// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Eez.Config;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Enr;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Channel = System.Threading.Channels.Channel;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;
using Libp2pPublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;
using NetworkForkId = Nethermind.Network.ForkId;
using EnrForkId = Nethermind.Network.Enr.ForkId;

namespace Nethermind.Eez.Network;

public sealed class EezPeerNetwork : IStoppableService, IAsyncDisposable
{
    private const int MaxDiscoveredPeers = 256;
    private readonly IEezConfig _config;
    private readonly IProtectedPrivateKey _nodeKey;
    private readonly IForkInfo _forks;
    private readonly IBlockTree _tree;
    private readonly ILogger _logger;
    private readonly ServiceProvider _services;
    private readonly SequencerPeerFactoryBuilder _builder;
    private readonly PeerStore _store = new(MaxDiscoveredPeers);
    private readonly SequencerGossipValidator _validator;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Channel<NodeRecord> _discovery = Channel.CreateBounded<NodeRecord>(new BoundedChannelOptions(32)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false
    });
    private readonly Channel<byte> _heads = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false
    });
    private ILocalPeer? _local;
    private PubsubRouter? _router;
    private ITopic? _topic;
    private Task? _discoveryTask;
    private readonly Lock _lifetime = new();
    private Task? _starting;
    private Task? _stopping;
    private Task? _disposing;

    public EezPeerNetwork(IEezConfig config, ISpecProvider specProvider,
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
        IForkInfo forks, IBlockTree tree, ITimestamper clock, ILogManager logManager)
    {
        _config = config;
        _nodeKey = nodeKey;
        _forks = forks;
        _tree = tree;
        _logger = logManager.GetClassLogger<EezPeerNetwork>();
        Codec = new SequencerHeadCodec(specProvider.ChainId, new Address(config.RegistryAddress!), config.RollupId,
            new Address(config.SequencerP2PSigner!));
        _validator = new SequencerGossipValidator(Codec, Tracker, clock);
        _validator.HeadAccepted += OnHead;
        _services = new ServiceCollection()
            .AddSingleton(_store)
            .AddSingleton(Codec)
            .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
            .AddSingleton<MultiplexerSettings>()
            .AddSingleton<MultistreamProtocol>()
            .AddSingleton(new PubsubSettings
            {
                LowestDegree = 5, MessageCacheTtl = 60000,
                AppSpecificWeight = 0, IPColocationFactorWeight = 0, BehaviorPenaltyWeight = 0,
                TopicScoreParams = new() { [Codec.Topic] = new TopicScoreParams { TopicWeight = 0 } },
                MaxRpcBytes = SequencerHeadCodec.MaxFrameSize,
                MaxOutboundRpcBytesPerPeer = SequencerHeadCodec.MaxFrameSize + 5L,
                MaxRemoteSubscriptionsPerPeer = 1,
                MaxRemoteSubscriptions = 64,
                PrunePeers = 0,
                AcceptPXThreshold = double.PositiveInfinity,
                MaxIwantResponseBytes = SequencerHeadCodec.MaxFrameSize,
                DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
                GetMessageId = _validator.MessageId
            })
            .AddSingleton<PubsubRouter>()
            .BuildServiceProvider();
        _builder = new SequencerPeerFactoryBuilder(_services);
    }

    public string Description => "EEZ signed-head overlay";
    internal SequencerHeadTracker Tracker { get; } = new();
    internal SequencerHeadCodec Codec { get; }
    internal Multiaddress[] ListenAddresses => _local is null ? [] : [.. _local.ListenAddresses];

    internal async Task Start(CancellationToken token)
    {
        Task starting;
        lock (_lifetime)
        {
            if (_stopping is not null) throw new InvalidOperationException("The EEZ overlay has stopped.");
            starting = _starting ??= StartCore(token);
        }
        try
        {
            await starting;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    private async Task StartCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using PrivateKey key = _nodeKey.Unprotect();
        IPeerFactory factory = _builder.Build();
        _local = factory.Create(new Identity(key.KeyBytes, KeyType.Secp256K1));
        _router = _services.GetRequiredService<PubsubRouter>();
        _router.VerifyMessage = (_, message) => Validate(message);
        _topic = _router.GetTopic(Codec.Topic);
        string family = IPAddress.Parse(_config.SequencerP2PHost).AddressFamily == AddressFamily.InterNetwork ? "ip4" : "ip6";
        Multiaddress listen = Multiaddress.Decode($"/{family}/{_config.SequencerP2PHost}/tcp/{_config.SequencerP2PPort}");
        await _local.StartListenAsync([listen], _cancellation.Token).WaitAsync(token);
        _store.OnNewPeer += ConfigurePeer;
        await _router.StartAsync(_local, _cancellation.Token);
        _topic.Subscribe();
        PeerStore store = _services.GetRequiredService<PeerStore>();
        foreach (string address in _config.SequencerP2PStaticPeers) Discover(store, Multiaddress.Decode(address), true);
        _discoveryTask = DiscoverLoop(store, _cancellation.Token);
        if (_logger.IsInfo) _logger.Info($"EEZ signed-head overlay listening on {_config.SequencerP2PHost}:{_config.SequencerP2PPort}.");
    }

    internal void Discover(NodeRecord record) => _discovery.Writer.TryWrite(record);

    private async Task DiscoverLoop(PeerStore store, CancellationToken token)
    {
        try
        {
            await foreach (NodeRecord record in _discovery.Reader.ReadAllAsync(token))
            {
                if (!TryAddress(record, out Multiaddress? address)) continue;
                try
                {
                    Discover(store, address, false);
                }
                catch (Libp2pException exception)
                {
                    if (_logger.IsDebug) _logger.Debug($"EEZ overlay peer admission refused: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void Discover(PeerStore store, Multiaddress address, bool trusted)
    {
        PeerId? id = address.GetPeerId();
        if (id is null || id == _local!.Identity.PeerId) return;
        store.Discover([address], trusted);
    }

    private void ConfigurePeer(Multiaddress[] addresses)
    {
        PeerId? id = addresses.Length == 0 ? null : addresses[0].GetPeerId();
        if (id is not null && _store.TryGetPeerInfo(id, out _))
            _store.SetSupportedProtocols(id, [PubsubRouter.GossipsubProtocolVersionV12, PubsubRouter.GossipsubProtocolVersionV11, PubsubRouter.GossipsubProtocolVersionV10]);
    }

    internal bool TryAddress(NodeRecord record, [NotNullWhen(true)] out Multiaddress? address)
    {
        address = null;
        IPAddress? ip = record.GetObj<IPAddress>(EnrContentKey.Ip);
        EnrForkId? advertised = record.GetValue<EnrForkId>(EnrContentKey.Eth);
        byte[]? encoded = record.GetObj<byte[]>("eez");
        CompressedPublicKey? key = record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);
        if (ip?.AddressFamily != AddressFamily.InterNetwork || advertised is null || encoded is null || key is null) return false;
        BlockHeader? head = _tree.Head?.Header ?? _tree.Genesis;
        if (head is null) return false;
        NetworkForkId local = _forks.GetForkId(head.Number, head.Timestamp);
        if (advertised.Value.Next != local.Next || !advertised.Value.ForkHash.AsSpan().SequenceEqual(local.HashBytes)) return false;
        try
        {
            RlpReader reader = new(encoded);
            int port = reader.DecodeInt();
            reader.CheckEnd();
            if (port is < 1 or > ushort.MaxValue) return false;
            Identity identity = new(new Libp2pPublicKey { Type = KeyType.Secp256K1, Data = ByteString.CopyFrom(key.Bytes) });
            address = Multiaddress.Decode($"/ip4/{ip}/tcp/{port}/p2p/{identity.PeerId}");
            return true;
        }
        catch (RlpReaderException)
        {
            return false;
        }
    }

    internal MessageValidity Validate(Message message) => _validator.Validate(message);

    private void OnHead() => _heads.Writer.TryWrite(0);

    internal async Task WaitForHead(TimeSpan delay, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(delay);
        try
        {
            await _heads.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
        }
    }

    public Task StopAsync()
    {
        lock (_lifetime) return _stopping ??= StopCore(_starting);
    }

    private async Task StopCore(Task? starting)
    {
        _validator.HeadAccepted -= OnHead;
        _store.OnNewPeer -= ConfigurePeer;
        await _cancellation.CancelAsync();
        if (starting is not null)
        {
            try { await starting; }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Debug($"EEZ overlay startup ended during shutdown: {exception.Message}");
            }
        }
        try
        {
            if (_discoveryTask is not null) await _discoveryTask;
        }
        finally
        {
            if (_local is not null)
            {
                await _local.DisposeAsync();
                _local = null;
            }
            _topic?.Unsubscribe();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetime) return new ValueTask(_disposing ??= DisposeCore());
    }

    private async Task DisposeCore()
    {
        using (_cancellation)
        await using (_builder)
        await using (_services)
            await StopAsync();
    }
}
