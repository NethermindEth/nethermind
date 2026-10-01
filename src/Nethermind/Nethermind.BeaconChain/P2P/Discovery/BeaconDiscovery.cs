// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
using Nethermind.BeaconChain.DataAvailability;
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
    internal const string IdentityMetadataKey = "p2pIdentityKey";

    /// <summary>The sequence of the last published local ENR, kept with the identity it signs.</summary>
    internal const string EnrSequenceMetadataKey = "p2pEnrSequence";

    internal static readonly TimeSpan TableSweepInterval = TimeSpan.FromSeconds(30);

    // Bounds how often a custodian request can force a sweep, since each one converts the whole table.
    private static readonly TimeSpan CustodianSweepMinInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CustodianSearchLogInterval = TimeSpan.FromMinutes(1);

    private const int CandidateCapacity = 256;

    /// <summary>Dial outcomes by address, shared with the peer manager, so an address whose dials failed is offered again only after its backoff.</summary>
    internal PeerDialHistory DialHistory { get; } = new(timestamper);

    private readonly ILogger _logger = logManager.GetClassLogger<BeaconDiscovery>();

    private readonly Lock _digestLock = new();
    private readonly Lock _enrLock = new();
    private ulong? _digestEpoch;
    private byte[] _currentDigest = [];
    private byte[]? _nextDigest;
    private EnrForkId? _currentForkId;

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

    private ulong[] _wantedColumns = [];
    private TaskCompletionSource _custodiansRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _custodianSearchLoggedAt = long.MinValue / 2;

    /// <summary>The local signed ENR, exposed for logging and diagnostics. Only valid once <see cref="Start"/> has returned.</summary>
    public NodeRecord LocalNodeRecord => _localEnr!.Current;

    /// <summary>This node's own custody groups and gossip subnets. Only valid once <see cref="Start"/> has returned.</summary>
    public LocalCustody LocalCustody { get; private set; } = null!;

    /// <summary>The sampled columns no connected peer custodies, as last reported to <see cref="RequestColumnCustodians"/>.</summary>
    internal IReadOnlyList<ulong> WantedColumns => Volatile.Read(ref _wantedColumns);

    /// <summary>
    /// Makes <see cref="DiscoverPeers"/> offer first the candidates custodying most of <paramref name="columns"/>, and sweeps
    /// the routing table early when a column is newly wanted; an empty list restores arrival order.
    /// </summary>
    /// <remarks>fulu/das-core.md: a node must retrieve every column it samples, and fulu/p2p-interface.md peers serve only the columns they custody.</remarks>
    internal void RequestColumnCustodians(IReadOnlyList<ulong> columns)
    {
        ulong[] wanted = [.. columns];
        ulong[] previous = Interlocked.Exchange(ref _wantedColumns, wanted);
        foreach (ulong column in wanted)
        {
            if (Array.IndexOf(previous, column) < 0)
            {
                long now = timestamper.UtcNow.Ticks;
                long loggedAt = Interlocked.Read(ref _custodianSearchLoggedAt);
                // A custodian set that keeps changing would otherwise log on every change.
                if (now - loggedAt >= CustodianSearchLogInterval.Ticks && Interlocked.CompareExchange(ref _custodianSearchLoggedAt, now, loggedAt) == loggedAt && _logger.IsInfo)
                {
                    _logger.Info($"No connected beacon chain peer custodies sampled columns [{string.Join(", ", wanted)}]; looking for custodians");
                }

                Interlocked.Exchange(ref _custodiansRequested, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                return;
            }
        }
    }

    /// <summary>Binds the discv5 UDP port and starts the Kademlia bootstrap and maintenance loops.</summary>
    public async Task Start(CancellationToken token)
    {
        if (_runCts is not null)
        {
            throw new InvalidOperationException($"{nameof(BeaconDiscovery)} is already started.");
        }

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve(token);
        IPAddress? advertised = AdvertisedAddress(ip);
        if (advertised is null && _logger.IsWarn) _logger.Warn("No external IPv4 address is known, so the beacon chain ENR advertises no endpoint and peers cannot dial this node.");
        NettyDiscoveryV5Handler handler = CreateDiscv5Services(advertised);

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
    /// Waiting candidates are offered by how many <see cref="WantedColumns"/> they custody (see <see cref="RequestColumnCustodians"/>).
    /// </remarks>
    public async IAsyncEnumerable<BeaconPeerCandidate> DiscoverPeers([EnumeratorCancellation] CancellationToken token)
    {
        System.Threading.Channels.Channel<Node> nodes = CreateNodeChannel();
        // The sweep waits on a full queue, so it must stop however the consumer stops.
        using CancellationTokenSource pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken pumpToken = pumpCancellation.Token;

        async Task PumpInsertsAsync()
        {
            await foreach (Node node in _nodeSource!.DiscoverNodes(pumpToken))
            {
                // Never waits, so a consumer parked at the target peer count cannot pause the lookups behind the source; the sweep re-offers a dropped node.
                nodes.Writer.TryWrite(node);
            }
        }

        Task pumps = Task.WhenAll(PumpInsertsAsync(), SweepTableAsync(() => _kademlia!.IterateNodes(), nodes.Writer, TableSweepInterval, CustodianSweepMinInterval, pumpToken))
            .ContinueWith(t => nodes.Writer.TryComplete(t.Exception?.GetBaseException()), CancellationToken.None);

        try
        {
            await foreach (BeaconPeerCandidate candidate in OfferByCustodyAsync(nodes.Reader, CreateCandidate, token))
            {
                yield return candidate;
            }
        }
        finally
        {
            await pumpCancellation.CancelAsync();
            await pumps;
        }
    }

    /// <summary>The queue from the node sources to the custody ranking; a full queue makes the table sweep wait, so only the ranking evicts a swept candidate.</summary>
    /// <remarks>fulu/das-core.md: a node must retrieve every column it samples, so a custodian must not be lost before it is ranked.</remarks>
    internal static System.Threading.Channels.Channel<Node> CreateNodeChannel() => System.Threading.Channels.Channel.CreateBounded<Node>(
        new System.Threading.Channels.BoundedChannelOptions(CandidateCapacity) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });

    /// <summary>Writes every routing-table node to <paramref name="writer"/> each <paramref name="sweepInterval"/>, and early when a column is newly wanted.</summary>
    /// <remarks>An early sweep is followed by at least <paramref name="minInterval"/> before the next, since each one converts the whole table.</remarks>
    internal async Task SweepTableAsync(Func<IEnumerable<Node>> iterateNodes, System.Threading.Channels.ChannelWriter<Node> writer, TimeSpan sweepInterval, TimeSpan minInterval, CancellationToken token)
    {
        Task requested = Volatile.Read(ref _custodiansRequested).Task;
        while (!token.IsCancellationRequested)
        {
            using (CancellationTokenSource sweepDelay = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                await Task.WhenAny(Task.Delay(sweepInterval, sweepDelay.Token), requested);
                await sweepDelay.CancelAsync();
            }

            token.ThrowIfCancellationRequested();
            bool early = requested.IsCompleted;
            // Read before the sweep and the pause, so a column newly wanted during either still wakes the next wait.
            requested = Volatile.Read(ref _custodiansRequested).Task;
            foreach (Node node in iterateNodes())
            {
                await writer.WriteAsync(node, token);
            }

            if (early)
            {
                await Task.Delay(minInterval, token);
            }
        }
    }

    /// <summary>Yields the candidates made from <paramref name="nodes"/>, those custodying most of <see cref="WantedColumns"/> first.</summary>
    internal async IAsyncEnumerable<BeaconPeerCandidate> OfferByCustodyAsync(System.Threading.Channels.ChannelReader<Node> nodes, Func<Node, BeaconPeerCandidate?> createCandidate, [EnumeratorCancellation] CancellationToken token)
    {
        CustodyRankedCandidates waiting = new(CandidateCapacity, c => 5 * DialHistory.Quality(c.Multiaddress) + c.ForkPreference);
        while (true)
        {
            IReadOnlyList<ulong> wanted = WantedColumns;
            for (int read = 0; read < CandidateCapacity && nodes.TryRead(out Node? node); read++)
            {
                if (createCandidate(node) is { } candidate)
                {
                    waiting.Add(candidate, wanted);
                }
            }

            if (waiting.TryTake(wanted, out BeaconPeerCandidate? next))
            {
                if (SelectDialableAddress(next) is { } dialable)
                {
                    yield return dialable;
                }
            }
            else if (!await nodes.WaitToReadAsync(token))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Recomputes the <c>eth2</c> fork id from the wall clock and republishes the ENR with a bumped sequence
    /// number when it changed. Call on epoch transitions so EIP-7892 BPO rotations propagate to peers.
    /// </summary>
    /// <returns><see langword="true"/> when a new ENR was published.</returns>
    public bool UpdateLocalEnr()
    {
        lock (_enrLock)
        {
            return UpdateLocalEnrCore();
        }
    }

    private bool UpdateLocalEnrCore()
    {
        ulong epoch = CurrentEpoch;
        EnrForkId forkId = EnrForkId.Compute(spec, epoch);
        byte[]? nextForkDigest = EnrForkId.NextForkDigest(spec, epoch);
        if (forkId.Equals(_localEnr!.ForkId) && Bytes.AreEqual(nextForkDigest ?? NfdEntry.NoneScheduled, _localEnr.NextForkDigest))
        {
            return false;
        }

        // EIP-778: persist before publication so a restart cannot reuse a sequence peers may already hold.
        PersistEnrSequence(LocalNodeRecord.EnrSequence + 1);
        if (!_localEnr.Update(forkId, nextForkDigest))
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

    internal static bool TryCreateCandidate(NodeRecord record, byte[] currentForkDigest, byte[]? nextForkDigest, [NotNullWhen(true)] out BeaconPeerCandidate? candidate, EnrForkId? localForkId = null)
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
        List<string> addresses = [$"/{ipProtocol}/{tcpEndpoint.Address}/tcp/{tcpEndpoint.Port}/p2p/{peerId}"];
        if (tcpEndpoint.AddressFamily == AddressFamily.InterNetwork && record.TryGetTcpEndpoint(AddressFamily.InterNetworkV6, out IPEndPoint? ipv6))
        {
            addresses.Add($"/ip6/{ipv6.Address}/tcp/{ipv6.Port}/p2p/{peerId}");
        }

        candidate = new BeaconPeerCandidate($"/{ipProtocol}/{tcpEndpoint.Address}/tcp/{tcpEndpoint.Port}/p2p/{peerId}", peerId, forkId.ForkDigest, record.EnrSequence, record.ToString())
        {
            Custody = PeerColumnCustody.ForRecord(record) ?? PeerColumnCustody.None,
            Addresses = addresses,
            ForkPreference = (Bytes.AreEqual(forkId.ForkDigest, currentForkDigest) ? 2 : 0) +
                (TryGetNextForkDigest(record, out byte[]? advertisedNext) &&
                    Bytes.AreEqual(advertisedNext ?? NfdEntry.NoneScheduled, nextForkDigest ?? NfdEntry.NoneScheduled) ? 1 : 0) +
                (localForkId is not null && forkId.NextForkEpoch == localForkId.NextForkEpoch &&
                    Bytes.AreEqual(forkId.NextForkVersion, localForkId.NextForkVersion) ? 1 : 0),
        };
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

    /// <summary>Decodes a record's <c>cgc</c> entry (fulu/p2p-interface.md); <see langword="false"/> when the entry is absent or malformed.</summary>
    internal static bool TryGetCustodyGroupCount(NodeRecord record, out ulong custodyGroupCount)
    {
        custodyGroupCount = 0;
        // A record parsed off the wire keeps an entry type it does not know as its raw RLP item.
        if (record.GetObj<byte[]>("cgc") is not { } value)
        {
            return false;
        }

        try
        {
            RlpReader reader = new(value);
            custodyGroupCount = reader.DecodeULong();
            return reader.Position == value.Length;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes a record's <c>nfd</c> entry: <see langword="false"/> when the entry is absent (a peer
    /// that has not adopted EIP-7892 yet), and <c>null</c> when present but advertising
    /// <see cref="NfdEntry.NoneScheduled"/> (no fork is upcoming for that peer).
    /// </summary>
    internal static bool TryGetNextForkDigest(NodeRecord record, out byte[]? nextForkDigest)
    {
        nextForkDigest = null;
        byte[]? value = record.GetObj<byte[]>("nfd");
        if (value is null)
        {
            return false;
        }

        ReadOnlySpan<byte> ssz = value;
        if (ssz.Length != NfdEntry.NoneScheduled.Length)
        {
            // Same wire quirk as the eth2 entry: a record parsed off the wire keeps the raw RLP of an
            // entry type it does not itself know how to decode.
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

        nextForkDigest = Bytes.AreEqual(ssz, NfdEntry.NoneScheduled) ? null : ssz.ToArray();
        return true;
    }

    private BeaconPeerCandidate? CreateCandidate(Node node)
    {
        if (node.Enr is not NodeRecord record)
        {
            return null;
        }

        try
        {
            (byte[] currentDigest, byte[]? nextDigest, EnrForkId localForkId) = AcceptedForkDigests();
            return TryCreateCandidate(record, currentDigest, nextDigest, out BeaconPeerCandidate? candidate, localForkId) ? SelectDialableAddress(candidate) : null;
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse discovered beacon chain ENR for {node:s}: {PeerManager.DescribeFailure(e)}");
            return null;
        }
    }

    internal BeaconPeerCandidate? SelectDialableAddress(BeaconPeerCandidate candidate)
    {
        string? selected = null;
        int quality = int.MinValue;
        foreach (string address in candidate.Addresses)
        {
            int score = DialHistory.Quality(address);
            if (DialHistory.CanDial(address) && score > quality)
            {
                selected = address;
                quality = score;
            }
        }

        return selected is null ? null : candidate with { Multiaddress = selected };
    }

    private (byte[] Current, byte[]? Next, EnrForkId LocalForkId) AcceptedForkDigests()
    {
        ulong epoch = CurrentEpoch;
        lock (_digestLock)
        {
            if (_digestEpoch != epoch)
            {
                _digestEpoch = epoch;
                _currentForkId = EnrForkId.Compute(spec, epoch);
                _currentDigest = _currentForkId.ForkDigest;
                _nextDigest = EnrForkId.NextForkDigest(spec, epoch);
            }

            return (_currentDigest, _nextDigest, _currentForkId!);
        }
    }

    private ulong CurrentEpoch => spec.GetEpoch(spec.GetSlotAtTime(timestamper.UnixTime.Seconds));

    /// <summary>The address the local ENR advertises: the external IPv4 address, since the libp2p host and the discv5 socket listen on IPv4 only.</summary>
    /// <remarks>consensus-specs v1.7.0-beta.2 networking Transport: advertised listening endpoints must be publicly dialable.</remarks>
    internal static IPAddress? AdvertisedAddress(IIPResolver.NethermindIp ip) => ip.ExternalIpV4;

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
    /// <param name="externalIp">The address the ENR advertises; <c>null</c> advertises no endpoint.</param>
    internal NettyDiscoveryV5Handler CreateDiscv5Services(IPAddress? externalIp)
    {
        CryptoRandom cryptoRandom = new();
        PrivateKey nodeKey = LoadOrCreateIdentity(cryptoRandom);
        LocalCustody = new LocalCustody(nodeKey.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement);
        ulong epoch = CurrentEpoch;
        BeaconNodeRecordProvider localEnr = new(nodeKey, externalIp, config.P2PPort, config.Discv5Port, EnrForkId.Compute(spec, epoch), LocalCustody.CustodyGroupCount, EnrForkId.NextForkDigest(spec, epoch),
            NextEnrSequence());
        Node currentNode = new(nodeKey.PublicKey, (externalIp ?? IPAddress.None).ToString(), config.P2PPort, config.Discv5Port, true);

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

    /// <summary>The bootnodes discovery will dial: the config override if set, otherwise the
    /// network's own records from the spec.</summary>
    internal List<Node> CreateBootNodes()
    {
        string[] enrs = string.IsNullOrWhiteSpace(config.Bootnodes)
            ? spec.Bootnodes
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
                if (_logger.IsWarn) _logger.Warn($"Unable to parse beacon chain bootnode ENR {enr}: {PeerManager.DescribeFailure(e)}");
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
        store.PutMetadata(IdentityMetadataKey, key.KeyBytes.AsSpan().ToArray());
        if (_logger.IsInfo) _logger.Info($"Generated new beacon chain P2P identity {DerivePeerId(key.CompressedPublicKey)}");
        return key;
    }

    /// <summary>Continues the sequence of the record last published under the persisted identity, and persists the new value.</summary>
    /// <remarks>EIP-778: a node increases the sequence whenever its record changes; a peer holding a higher one ignores a lower one.</remarks>
    private ulong NextEnrSequence()
    {
        ulong sequence = (store.GetMetadata(EnrSequenceMetadataKey) is { Length: sizeof(ulong) } stored ? BinaryPrimitives.ReadUInt64BigEndian(stored) : 0) + 1;
        PersistEnrSequence(sequence);
        return sequence;
    }

    private void PersistEnrSequence(ulong sequence)
    {
        byte[] value = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, sequence);
        store.PutMetadata(EnrSequenceMetadataKey, value);
    }

    private sealed class NodeKeyWrapper(PrivateKey key) : IProtectedPrivateKey
    {
        public PublicKey PublicKey => key.PublicKey;
        public CompressedPublicKey CompressedPublicKey => key.CompressedPublicKey;
        public PrivateKey Unprotect() => key;
    }
}
