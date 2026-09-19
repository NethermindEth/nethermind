// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Google.Protobuf;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using Discv5KademliaModule = Nethermind.Network.Discovery.Discv5.Kademlia.KademliaModule;
using IChannel = DotNetty.Transport.Channels.IChannel;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;
using Libp2pPublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

[assembly: InternalsVisibleTo("Nethermind.BeaconChain.Test")]

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>
/// A plugin-owned discv5 instance discovering beacon chain peers on its own UDP port, independent
/// of the execution-layer discovery stack.
/// </summary>
/// <remarks>
/// Composes the same discv5 Kademlia services as <c>DiscoveryV5App</c> in a private container, but with
/// <see cref="AcceptAllDiscv5RecordFilter"/> (consensus-only ENRs are the peers we are after), the persisted
/// libp2p identity as the node key (so the ENR's secp256k1 key matches the libp2p peer id), and a local ENR
/// carrying the <c>eth2</c> fork id entry. Discovered ENRs are filtered by fork digest and converted to dialable
/// libp2p multiaddrs.
/// </remarks>
public sealed class BeaconDiscovery(
    IBeaconChainConfig config,
    BeaconChainSpec spec,
    BeaconChainStore store,
    IIPResolver ipResolver,
    ITimestamper timestamper,
    ILogManager logManager) : IAsyncDisposable
{
    /// <summary>Same metadata key as the libp2p host so both stacks share one secp256k1 identity.</summary>
    private const string IdentityMetadataKey = "p2pIdentityKey";

    private static readonly TimeSpan TableSweepInterval = TimeSpan.FromMinutes(2);

    private readonly ILogger _logger = logManager.GetClassLogger<BeaconDiscovery>();

    private readonly Lock _digestLock = new();
    private ulong? _digestEpoch;
    private byte[] _currentDigest = [];
    private byte[]? _nextDigest;

    private BeaconNodeRecordProvider? _localEnr;
    private IContainer? _discv5Services;
    private IKademliaAdapter? _adapter;
    private IKademlia<PublicKey, Node>? _kademlia;
    private IKademliaNodeSource? _nodeSource;
    private NettyDiscoveryV5Handler? _handler;

    private MultithreadEventLoopGroup? _group;
    private IChannel? _channel;
    private CancellationTokenSource? _runCts;
    private Task _runTask = Task.CompletedTask;
    private bool _stopped;

    /// <summary>The local signed ENR, exposed for logging and diagnostics. Only valid once <see cref="Start"/> has returned.</summary>
    public NodeRecord LocalNodeRecord => _localEnr!.Current;

    /// <summary>Binds the discv5 UDP port and starts the Kademlia bootstrap and maintenance loops.</summary>
    public async Task Start(CancellationToken token)
    {
        if (_runCts is not null)
        {
            throw new InvalidOperationException($"{nameof(BeaconDiscovery)} is already started.");
        }

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve(token);
        NettyDiscoveryV5Handler handler = CreateDiscv5Services(ip.ExternalIp);

        _group = new MultithreadEventLoopGroup(1);
        Bootstrap bootstrap = new Bootstrap()
            .Group(_group)
            .Option(ChannelOption.Allocator, NethermindBuffers.DiscoveryAllocator)
            .Option(ChannelOption.RcvbufAllocator, new FixedRecvByteBufAllocator(2048 * 2));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            bootstrap.ChannelFactory(static () => new SocketDatagramChannel(AddressFamily.InterNetwork));
        }
        else
        {
            bootstrap.Channel<SocketDatagramChannel>();
        }

        bootstrap.Handler(new ActionChannelInitializer<IDatagramChannel>(channel =>
        {
            handler.InitializeChannel(channel);
            channel.Pipeline.AddLast(handler);
        }));

        _channel = await bootstrap.BindAsync(config.Discv5Port);
        _runTask = RunDiscovery(_runCts.Token);
        if (_logger.IsInfo) _logger.Info($"Beacon chain discv5 listening on UDP port {config.Discv5Port}, local ENR: {LocalNodeRecord}");
    }

    /// <summary>
    /// Streams dialable peer candidates with a matching fork digest (the current digest, or the
    /// upcoming one near a digest rotation).
    /// </summary>
    /// <remarks>
    /// Merges two sources: routing-table inserts (fresh discoveries) and a periodic sweep over the
    /// whole routing table. The sweep matters: once the table stabilizes, inserts stop, and with the
    /// low dial-success rate against mainnet peers the consumer would otherwise starve — known
    /// candidates must be re-offered so dropped or previously-failed peers get re-dialed.
    /// </remarks>
    public async IAsyncEnumerable<BeaconPeerCandidate> DiscoverPeers([EnumeratorCancellation] CancellationToken token)
    {
        System.Threading.Channels.Channel<Node> nodes = System.Threading.Channels.Channel.CreateBounded<Node>(
            new System.Threading.Channels.BoundedChannelOptions(256) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });

        async Task PumpInsertsAsync()
        {
            await foreach (Node node in _nodeSource!.DiscoverNodes(token))
            {
                await nodes.Writer.WriteAsync(node, token);
            }
        }

        async Task PumpTableSweepsAsync()
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TableSweepInterval, token);
                foreach (Node node in _kademlia!.IterateNodes())
                {
                    nodes.Writer.TryWrite(node);
                }
            }
        }

        Task pumps = Task.WhenAll(PumpInsertsAsync(), PumpTableSweepsAsync())
            .ContinueWith(t => nodes.Writer.TryComplete(t.Exception?.GetBaseException()), CancellationToken.None);

        await foreach (Node node in nodes.Reader.ReadAllAsync(token))
        {
            BeaconPeerCandidate? candidate = CreateCandidate(node);
            if (candidate is not null)
            {
                yield return candidate;
            }
        }

        await pumps;
    }

    /// <summary>
    /// Recomputes the <c>eth2</c> fork id from the wall clock and republishes the ENR with a bumped sequence
    /// number when it changed. Call on epoch transitions so EIP-7892 BPO rotations propagate to peers.
    /// </summary>
    /// <returns><see langword="true"/> when a new ENR was published.</returns>
    public bool UpdateLocalEnr()
    {
        if (!_localEnr!.Update(EnrForkId.Compute(spec, CurrentEpoch)))
        {
            return false;
        }

        if (_logger.IsInfo) _logger.Info($"Updated beacon chain ENR to {_localEnr.ForkId}, sequence {LocalNodeRecord.EnrSequence}");
        return true;
    }

    public async Task Stop()
    {
        if (_runCts is null || _stopped)
        {
            return;
        }

        _stopped = true;
        await _runCts.CancelAsync();
        try
        {
            await _runTask;
        }
        catch (OperationCanceledException)
        {
        }

        if (_adapter is not null)
        {
            await _adapter.DisposeAsync();
        }

        _handler?.Close();
        if (_channel is not null)
        {
            await _channel.CloseAsync();
        }

        if (_group is not null)
        {
            await _group.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Stop();
        _runCts?.Dispose();
        if (_discv5Services is not null)
        {
            await _discv5Services.DisposeAsync();
        }
    }

    internal static bool TryCreateCandidate(NodeRecord record, byte[] currentForkDigest, byte[]? nextForkDigest, [NotNullWhen(true)] out BeaconPeerCandidate? candidate)
    {
        candidate = null;
        if (!TryGetForkId(record, out EnrForkId? forkId))
        {
            return false;
        }

        if (!Bytes.AreEqual(forkId.ForkDigest, currentForkDigest) &&
            (nextForkDigest is null || !Bytes.AreEqual(forkId.ForkDigest, nextForkDigest)))
        {
            return false;
        }

        CompressedPublicKey? publicKey = record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1);
        if (!record.TryGetTcpEndpoint(out IPEndPoint? tcpEndpoint) || publicKey is null)
        {
            return false;
        }

        string peerId = DerivePeerId(publicKey);
        string ipProtocol = tcpEndpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "ip6" : "ip4";
        candidate = new BeaconPeerCandidate($"/{ipProtocol}/{tcpEndpoint.Address}/tcp/{tcpEndpoint.Port}/p2p/{peerId}", peerId, forkId.ForkDigest, record.EnrSequence);
        return true;
    }

    /// <summary>Derives the libp2p peer id from a compressed secp256k1 public key.</summary>
    internal static string DerivePeerId(CompressedPublicKey publicKey)
        => new PeerId(new Libp2pPublicKey { Type = KeyType.Secp256K1, Data = ByteString.CopyFrom(publicKey.Bytes) }).ToString();

    internal static bool TryGetForkId(NodeRecord record, [NotNullWhen(true)] out EnrForkId? forkId)
    {
        forkId = null;
        byte[]? value = record.GetObj<byte[]>(EnrContentKey.Eth2);
        if (value is null)
        {
            return false;
        }

        ReadOnlySpan<byte> ssz = value;
        if (ssz.Length != EnrForkId.SszLength)
        {
            // Records parsed from the wire keep the raw RLP of unknown entries, so unwrap the byte string.
            try
            {
                RlpReader reader = new(value);
                ssz = reader.DecodeByteArraySpan();
            }
            catch (RlpException)
            {
                return false;
            }
        }

        return EnrForkId.TryDecode(ssz, out forkId);
    }

    private BeaconPeerCandidate? CreateCandidate(Node node)
    {
        if (node.Enr is not NodeRecord record)
        {
            return null;
        }

        try
        {
            (byte[] currentDigest, byte[]? nextDigest) = AcceptedForkDigests();
            return TryCreateCandidate(record, currentDigest, nextDigest, out BeaconPeerCandidate? candidate) ? candidate : null;
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse discovered beacon chain ENR for {node:s}: {e}");
            return null;
        }
    }

    private (byte[] Current, byte[]? Next) AcceptedForkDigests()
    {
        ulong epoch = CurrentEpoch;
        lock (_digestLock)
        {
            if (_digestEpoch != epoch)
            {
                _digestEpoch = epoch;
                _currentDigest = ForkDigest.Compute(spec, epoch);
                ulong nextEpoch = EnrForkId.NextDigestEpoch(spec, epoch);
                _nextDigest = nextEpoch == Presets.FarFutureEpoch ? null : ForkDigest.Compute(spec, nextEpoch);
            }

            return (_currentDigest, _nextDigest);
        }
    }

    private ulong CurrentEpoch => spec.GetEpoch(spec.GetSlotAtTime(timestamper.UnixTime.Seconds));

    private async Task RunDiscovery(CancellationToken token)
    {
        Task adapterTask = _adapter!.RunAsync(token);
        try
        {
            await _kademlia!.Run(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Beacon chain discv5 loop failed.", e);
        }
        finally
        {
            await adapterTask;
        }
    }

    /// <summary>Builds the local ENR and the private discv5 service graph, both of which need the resolved external IP.</summary>
    /// <remarks>Internal so a test can resolve the graph without binding a socket or reaching bootnodes.</remarks>
    internal NettyDiscoveryV5Handler CreateDiscv5Services(IPAddress externalIp)
    {
        CryptoRandom cryptoRandom = new();
        PrivateKey nodeKey = LoadOrCreateIdentity(cryptoRandom);
        BeaconNodeRecordProvider localEnr = new(nodeKey, externalIp, config.P2PPort, config.Discv5Port, EnrForkId.Compute(spec, CurrentEpoch));
        Node currentNode = new(nodeKey.PublicKey, externalIp.ToString(), config.P2PPort, config.Discv5Port, true);

        IContainer discv5Services = new ContainerBuilder()
            .AddModule(new Discv5KademliaModule(currentNode, CreateBootNodes()))
            .AddSingleton<ILogManager>(logManager)
            .AddSingleton<IDiscoveryConfig>(new DiscoveryConfig())
            .AddSingleton<ITimestamper>(timestamper)
            .AddSingleton<ICryptoRandom>(cryptoRandom, takeOwnership: true)
            .AddSingleton<IEcdsa>(new Ecdsa())
            .AddKeyedSingleton<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey, new NodeKeyWrapper(nodeKey))
            .AddSingleton<INodeRecordProvider>(localEnr)
            .AddSingleton<IDiscv5RecordFilter>(AcceptAllDiscv5RecordFilter.Instance)
            .AddSingleton<IForkInfo>(PermissiveForkInfo.Instance)
            // The same resolver the local ENR was built from, so the adapter advertises that one address.
            .AddSingleton(ipResolver)
            .AddSingleton(new NetworkListenerState(new NetworkConfig(), ipResolver, logManager))
            .Build();

        _localEnr = localEnr;
        _discv5Services = discv5Services;
        _adapter = discv5Services.Resolve<IKademliaAdapter>();
        _kademlia = discv5Services.Resolve<IKademlia<PublicKey, Node>>();
        _nodeSource = discv5Services.Resolve<IKademliaNodeSource>();
        _handler = discv5Services.Resolve<NettyDiscoveryV5Handler>();
        return _handler;
    }

    private List<Node> CreateBootNodes()
    {
        string[] enrs = string.IsNullOrWhiteSpace(config.Bootnodes)
            ? BeaconBootnodes.ForChainId(spec.ChainId)
            : config.Bootnodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<Node> bootNodes = new(enrs.Length);
        foreach (string enr in enrs)
        {
            try
            {
                if (Node.TryFromDiscoveryEnr(NodeRecord.FromEnrString(enr), out Node? node))
                {
                    node.IsBootnode = true;
                    bootNodes.Add(node);
                }
                else if (_logger.IsWarn)
                {
                    _logger.Warn($"Skipping beacon chain bootnode ENR without a usable discovery endpoint: {enr}");
                }
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Unable to parse beacon chain bootnode ENR {enr}: {e.Message}");
            }
        }

        if (_logger.IsDebug) _logger.Debug($"Beacon chain discv5 accepted {bootNodes.Count}/{enrs.Length} bootnodes.");
        return bootNodes;
    }

    private PrivateKey LoadOrCreateIdentity(ICryptoRandom cryptoRandom)
    {
        byte[]? keyBytes = store.GetMetadata(IdentityMetadataKey);
        if (keyBytes is not null)
        {
            return new PrivateKey(keyBytes);
        }

        using PrivateKeyGenerator generator = new(cryptoRandom);
        PrivateKey key = generator.Generate();
        store.PutMetadata(IdentityMetadataKey, key.KeyBytes);
        if (_logger.IsInfo) _logger.Info($"Generated new beacon chain P2P identity {DerivePeerId(key.CompressedPublicKey)}");
        return key;
    }

    private sealed class NodeKeyWrapper(PrivateKey key) : IProtectedPrivateKey
    {
        public PublicKey PublicKey => key.PublicKey;
        public CompressedPublicKey CompressedPublicKey => key.CompressedPublicKey;
        public PrivateKey Unprotect() => key;
    }
}
