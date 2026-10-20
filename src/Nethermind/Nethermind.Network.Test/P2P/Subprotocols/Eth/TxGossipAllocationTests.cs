// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using ShouldGossip = Nethermind.TxPool.ShouldGossip;
using V65PooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

/// <summary>
/// Steady-state heap allocation gates for plain-transaction gossip: eth/68 announcements, the pooled-transaction
/// round trip into a real <see cref="TxPool.TxPool"/>, duplicate deliveries, serving pooled transactions and the
/// broadcast tick.
/// </summary>
/// <remarks>
/// Everything runs on the test thread (<see cref="RunImmediatelyScheduler"/>, a manual broadcast timer and a loopback
/// session that serializes and releases each outbound message), so <see cref="GC.GetAllocatedBytesForCurrentThread"/>
/// sees the whole path. Wire packets are built before each measured window; only handler and pool work is counted.
/// Each scenario runs once on a warm-up batch so caches, pools and lazily-sized per-peer state are populated, then on
/// fresh batches that are measured.
///
/// Logging is <see cref="NullLogManager"/>: <see cref="LimboLogs"/> reports trace as enabled, and the trace messages it
/// would format cost more than the whole path. The ceilings are the measured steady state with a few percent of
/// headroom; a change that removes allocations lowers them.
/// </remarks>
[TestFixture]
[NonParallelizable]
[ReleaseOnly("DEBUG builds capture a stack trace per ArrayPoolList, so the allocation ceilings do not apply.")]
public class TxGossipAllocationTests
{
    private const int Peers = 8;
    private const int SendersPerBatch = 32;
    private const int NoncesPerSender = 4;
    private const int TxsPerBatch = SendersPerBatch * NoncesPerSender;

    // Allocation noise only ever adds bytes (pool trims, dictionary resizes), so the minimum over runs is the steady state.
    // Each run grows the pool by a batch and its collections resize in most of the first four runs, so fewer runs than
    // this may never see a resize-free window.
    private const int MeasuredRuns = 6;

    private ISpecProvider _specProvider = null!;
    private IEthereumEcdsa _ecdsa = null!;
    private TestReadOnlyStateProvider _state = null!;
    private IMessageSerializationService _serializer = null!;
    private ISyncServer _syncServer = null!;
    private TxPool.TxPool _txPool = null!;
    private TxBroadcaster _broadcaster = null!;
    private ManualTimer _broadcastTimer = null!;
    private LoopbackSession[] _sessions = null!;
    private Eth68ProtocolHandler[] _handlers = null!;
    private int _nextSender;
    private bool _diagTracerWasEnabled;

    [SetUp]
    public void Setup()
    {
        // Other fixtures leave the process-wide tracer on; it formats every sent message, which production does not.
        _diagTracerWasEnabled = NetworkDiagTracer.IsEnabled;
        NetworkDiagTracer.IsEnabled = false;
        _specProvider = new TestSpecProvider(Prague.Instance);
        _ecdsa = new EthereumEcdsa(_specProvider.ChainId);
        _state = new TestReadOnlyStateProvider();
        _serializer = Build.A.SerializationService().WithEth68().TestObject;
        _nextSender = 0;

        IBlockTree blockTree = Build.A.BlockTree(_specProvider).OfChainLength(1).TestObject;
        ChainHeadSpecProvider headSpec = new(_specProvider, blockTree);
        IComparer<Transaction> comparer = new TransactionComparerProvider(_specProvider, blockTree).GetDefaultComparer();
        TxPoolConfig txPoolConfig = new() { Size = 4 * TxsPerBatch * 4, GasLimit = 1_000_000 };

        _txPool = new TxPool.TxPool(
            _ecdsa,
            new BlobTxStorage(),
            new FixedHeadInfo(headSpec, _state),
            txPoolConfig,
            new TxValidator(_specProvider.ChainId),
            new SpecChangeTxValidator(_specProvider.ChainId),
            NullLogManager.Instance,
            comparer,
            new FrameTxWidthLedger(txPoolConfig, NullLogManager.Instance),
            ShouldGossip.Instance);

        ManualTimerFactory timerFactory = new();
        _broadcastTimer = timerFactory.Timer;
        _broadcaster = new TxBroadcaster(comparer, timerFactory, txPoolConfig, new FixedHeadInfo(headSpec, _state), NullLogManager.Instance, ShouldGossip.Instance);

        Block genesis = Build.A.Block.Genesis.TestObject;
        _syncServer = Substitute.For<ISyncServer>();
        _syncServer.Head.Returns(genesis.Header);
        _syncServer.Genesis.Returns(genesis.Header);

        _sessions = new LoopbackSession[Peers];
        _handlers = new Eth68ProtocolHandler[Peers];
        for (int i = 0; i < Peers; i++)
        {
            _sessions[i] = new LoopbackSession(_serializer, new Node(TestItem.PublicKeys[i], new IPEndPoint(IPAddress.Loopback, 30303 + i)));
            _handlers[i] = new Eth68ProtocolHandler(
                _sessions[i],
                _serializer,
                new NodeStatsManager(Substitute.For<ITimerFactory>(), NullLogManager.Instance),
                _syncServer,
                RunImmediatelyScheduler.Instance,
                _txPool,
                Substitute.For<IGossipPolicy>(),
                new ForkInfo(_specProvider, _syncServer),
                NullLogManager.Instance,
                txPoolConfig,
                headSpec,
                ShouldGossip.Instance);
            _handlers[i].Init();
            ReceiveStatus(_handlers[i], genesis);
            _broadcaster.AddPeer(_handlers[i]);
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (Eth68ProtocolHandler handler in _handlers) handler.Dispose();
        _broadcaster.Dispose();
        _syncServer.Dispose();
        await _txPool.DisposeAsync();
        NetworkDiagTracer.IsEnabled = _diagTracerWasEnabled;
    }

    [Test]
    public void Pooled_transaction_round_trip_allocations()
    {
        RunRoundTrip(CreateBatch());
        RoundTripAllocations measured = RunRoundTrip(CreateBatch());
        for (int i = 1; i < MeasuredRuns; i++) measured = RoundTripAllocations.Min(measured, RunRoundTrip(CreateBatch()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(measured.AnnouncePerHashPerPeer, Is.LessThanOrEqualTo(10), "announce, bytes per hash per peer");
            Assert.That(measured.DeliverPerTx, Is.LessThanOrEqualTo(1440), "deliver, bytes per tx");
            Assert.That(measured.DuplicatePerTxPerPeer, Is.LessThanOrEqualTo(8), "duplicate, bytes per tx per peer");
            Assert.That(measured.ServePerTx, Is.LessThanOrEqualTo(8), "serve, bytes per tx");
        }
    }

    [Test]
    public void Broadcast_tick_allocations()
    {
        RunBroadcastTick(CreateBatch());
        double perPeerPerTick = double.MaxValue;
        for (int i = 0; i < MeasuredRuns; i++) perPeerPerTick = Math.Min(perPeerPerTick, RunBroadcastTick(CreateBatch()));

        Assert.That(perPeerPerTick, Is.LessThanOrEqualTo(170), "bytes per peer per tick");
    }

    private RoundTripAllocations RunRoundTrip(Transaction[] txs)
    {
        // Every peer announces the batch; the first announcer is asked for it, the rest are kept as fallbacks.
        ZeroPacket[] announcements = new ZeroPacket[Peers];
        for (int i = 0; i < Peers; i++) announcements[i] = Packet(Announcement(txs), Eth68MessageCode.NewPooledTransactionHashes);
        foreach (LoopbackSession session in _sessions) session.ClearRequests();

        long announce = MeasureHandling(announcements);

        ZeroPacket[] responses = new ZeroPacket[Peers];
        int requestedTxs = 0;
        for (int i = 0; i < Peers; i++)
        {
            LoopbackSession session = _sessions[i];
            if (session.RequestCount == 0) continue;
            Assert.That(session.RequestCount, Is.EqualTo(1), "harness expects one pooled-transactions request per peer");
            Transaction[] requested = Requested(txs, session.RequestedHashes);
            requestedTxs += requested.Length;
            responses[i] = Packet(
                new PooledTransactionsMessage(session.LastRequestId, new V65PooledTransactionsMessage(new ArrayPoolList<Transaction>(requested.Length, requested))),
                Eth66MessageCode.PooledTransactions);
        }
        Assert.That(requestedTxs, Is.EqualTo(txs.Length), "every announced tx is requested exactly once");

        long deliver = MeasureHandling(responses);
        Assert.That(_txPool.GetPendingTransactionsCount(), Is.GreaterThanOrEqualTo(txs.Length), "batch admitted");

        ZeroPacket[] duplicates = new ZeroPacket[Peers];
        for (int i = 0; i < Peers; i++)
        {
            if (responses[i] is not null) continue;
            duplicates[i] = Packet(new TransactionsMessage(new ArrayPoolList<Transaction>(txs.Length, txs)), Eth62MessageCode.Transactions);
        }
        int duplicatePeers = Peers - 1;
        long duplicate = MeasureHandling(duplicates);

        ValueHash256[] hashes = new ValueHash256[txs.Length];
        for (int i = 0; i < txs.Length; i++) hashes[i] = txs[i].Hash!;
        ZeroPacket[] serveRequests = new ZeroPacket[Peers];
        serveRequests[Peers - 1] = Packet(
            new GetPooledTransactionsMessage(new ArrayPoolList<ValueHash256>(hashes.Length, hashes)),
            Eth66MessageCode.GetPooledTransactions);
        long servedBefore = _sessions[Peers - 1].ServedTransactions;
        long serve = MeasureHandling(serveRequests);
        Assert.That(_sessions[Peers - 1].ServedTransactions - servedBefore, Is.EqualTo(txs.Length), "every requested tx served");

        return new RoundTripAllocations(
            AnnouncePerHashPerPeer: (double)announce / (txs.Length * Peers),
            DeliverPerTx: (double)deliver / txs.Length,
            DuplicatePerTxPerPeer: (double)duplicate / (txs.Length * duplicatePeers),
            ServePerTx: (double)serve / txs.Length);
    }

    private double RunBroadcastTick(Transaction[] txs)
    {
        foreach (Transaction tx in txs) _broadcaster.Broadcast(tx, isPersistent: false);
        long before = 0;
        foreach (LoopbackSession session in _sessions) before += session.AnnouncedTransactions;

        long start = GC.GetAllocatedBytesForCurrentThread();
        _broadcastTimer.Fire();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        long announced = -before;
        foreach (LoopbackSession session in _sessions) announced += session.AnnouncedTransactions;
        Assert.That(announced, Is.EqualTo((long)txs.Length * Peers), "every peer is told about every tx");

        return (double)allocated / Peers;
    }

    private long MeasureHandling(ZeroPacket?[] packets)
    {
        long allocated = 0;
        for (int i = 0; i < packets.Length; i++)
        {
            ZeroPacket? packet = packets[i];
            if (packet is null) continue;

            long start = GC.GetAllocatedBytesForCurrentThread();
            _handlers[i].HandleMessage(packet);
            allocated += GC.GetAllocatedBytesForCurrentThread() - start;

            packet.Content.Release();
        }
        return allocated;
    }

    private Transaction[] CreateBatch()
    {
        Transaction[] txs = new Transaction[TxsPerBatch];
        for (int s = 0; s < SendersPerBatch; s++)
        {
            PrivateKey sender = TestItem.PrivateKeys[_nextSender++];
            _state.CreateAccount(sender.Address, 1_000.Ether);
            for (int n = 0; n < NoncesPerSender; n++)
            {
                int i = s * NoncesPerSender + n;
                txs[i] = Build.A.Transaction
                    .WithType((TxType)(i % 3))
                    .WithChainId(_specProvider.ChainId)
                    .WithNonce((ulong)n)
                    .WithGasLimit(30_000)
                    .WithGasPrice(10.GWei)
                    .WithMaxFeePerGas(10.GWei)
                    .WithMaxPriorityFeePerGas(1.GWei)
                    .WithTo(TestItem.AddressB)
                    .WithData(new byte[i % 2 == 0 ? 0 : 68])
                    .SignedAndResolved(_ecdsa, sender)
                    .TestObject;
            }
        }
        return txs;
    }

    private static NewPooledTransactionHashesMessage68 Announcement(Transaction[] txs)
    {
        ArrayPoolList<byte> types = [with(txs.Length)];
        ArrayPoolList<int> sizes = [with(txs.Length)];
        ArrayPoolList<ValueHash256> hashes = [with(txs.Length)];
        foreach (Transaction tx in txs)
        {
            types.Add((byte)tx.Type);
            sizes.Add(tx.GetLength());
            hashes.Add(tx.Hash!);
        }
        return new NewPooledTransactionHashesMessage68(types, sizes, hashes);
    }

    private static Transaction[] Requested(Transaction[] txs, ReadOnlySpan<ValueHash256> hashes)
    {
        Transaction[] requested = new Transaction[hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
        {
            foreach (Transaction tx in txs)
            {
                if (tx.Hash!.ValueHash256 == hashes[i]) requested[i] = tx;
            }
            Assert.That(requested[i], Is.Not.Null, "requested hash was never announced");
        }
        return requested;
    }

    private ZeroPacket Packet<T>(T message, int code) where T : MessageBase
    {
        IByteBuffer buffer = _serializer.ZeroSerialize(message);
        buffer.ReadByte();
        (message as IDisposable)?.Dispose();
        return new ZeroPacket(buffer) { PacketType = (byte)code };
    }

    private void ReceiveStatus(Eth68ProtocolHandler handler, Block genesis)
    {
        StatusMessage status = new() { GenesisHash = genesis.Hash, BestHash = genesis.Hash };
        ZeroPacket packet = Packet(status, Eth62MessageCode.Status);
        handler.HandleMessage(packet);
        packet.Content.Release();
    }

    private readonly record struct RoundTripAllocations(double AnnouncePerHashPerPeer, double DeliverPerTx, double DuplicatePerTxPerPeer, double ServePerTx)
    {
        public static RoundTripAllocations Min(RoundTripAllocations a, RoundTripAllocations b) => new(
            Math.Min(a.AnnouncePerHashPerPeer, b.AnnouncePerHashPerPeer),
            Math.Min(a.DeliverPerTx, b.DeliverPerTx),
            Math.Min(a.DuplicatePerTxPerPeer, b.DuplicatePerTxPerPeer),
            Math.Min(a.ServePerTx, b.ServePerTx));
    }

    /// <summary>Head info with fixed values, so reading it on the measured path allocates nothing.</summary>
    private sealed class FixedHeadInfo(IChainHeadSpecProvider specProvider, IReadOnlyStateProvider state) : IChainHeadInfoProvider
    {
        public IChainHeadSpecProvider SpecProvider => specProvider;
        public IReadOnlyStateProvider ReadOnlyStateProvider => state;
        public bool TryGetHeadState(out BlockHeader? head, out IReadOnlyStateProvider? headState)
        {
            head = null;
            headState = null;
            return false;
        }
        public ulong HeadNumber => 1;
        public ulong HeadTimestamp => 1;
        public ulong? BlockGasLimit => 30_000_000;
        public UInt256 CurrentBaseFee => UInt256.Zero;
        public UInt256 NextBaseFee => UInt256.Zero;
        public UInt256 CurrentFeePerBlobGas => UInt256.Zero;
        public ProofVersion CurrentProofVersion => ProofVersion.V0;
        public bool IsSyncing => false;
        public bool IsProcessingBlock => false;
        public bool IsBuildingBlock => false;
        public event EventHandler<BlockReplacementEventArgs>? HeadChanged { add { } remove { } }
    }

    private sealed class ManualTimerFactory : ITimerFactory
    {
        public ManualTimer Timer { get; } = new();

        public Core.Timers.ITimer CreateTimer(TimeSpan interval)
        {
            Timer.Interval = interval;
            return Timer;
        }
    }

    private sealed class ManualTimer : Core.Timers.ITimer
    {
        public bool AutoReset { get; set; }
        public bool Enabled { get; set; }
        public TimeSpan Interval { get; set; }
        public double IntervalMilliseconds
        {
            get => Interval.TotalMilliseconds;
            set => Interval = TimeSpan.FromMilliseconds(value);
        }

        public event EventHandler? Elapsed;

        public void Start() => Enabled = true;
        public void Stop() => Enabled = false;
        public void Dispose() => Enabled = false;
        public void Fire() => Elapsed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Session that serializes every outbound message into a pooled buffer and releases it, like the real send path,
    /// and records pooled-transaction requests in preallocated storage so the harness can answer them.
    /// </summary>
    private sealed class LoopbackSession(IMessageSerializationService serializer, Node node) : ISession
    {
        private readonly ValueHash256[] _requestedHashes = new ValueHash256[1024];
        private int _requestedHashCount;

        public int RequestCount { get; private set; }
        public long LastRequestId { get; private set; }
        public ReadOnlySpan<ValueHash256> RequestedHashes => _requestedHashes.AsSpan(0, _requestedHashCount);
        public long ServedTransactions { get; private set; }
        public long AnnouncedTransactions { get; private set; }

        public void ClearRequests()
        {
            RequestCount = 0;
            _requestedHashCount = 0;
        }

        public int DeliverMessage<T>(T message) where T : P2PMessage
        {
            // Serialize first: a message the real session cannot send must not count as delivered.
            IByteBuffer buffer = serializer.ZeroSerialize(message);
            int size = buffer.ReadableBytes;
            buffer.Release();

            switch (message)
            {
                case GetPooledTransactionsMessage request:
                    RequestCount++;
                    LastRequestId = request.RequestId;
                    foreach (ValueHash256 hash in request.EthMessage.Hashes) _requestedHashes[_requestedHashCount++] = hash;
                    break;
                case PooledTransactionsMessage response:
                    ServedTransactions += response.EthMessage.Transactions.Count;
                    break;
                case NewPooledTransactionHashesMessage68 announcement:
                    AnnouncedTransactions += announcement.Hashes.Count;
                    break;
            }

            message.Dispose();
            return size;
        }

        public byte P2PVersion => 5;
        public SessionState State => SessionState.Initialized;
        public SessionState BestStateReached => SessionState.Initialized;
        public bool IsClosing => false;
        public PublicKey RemoteNodeId => node.Id;
        public PublicKey ObsoleteRemoteNodeId => null!;
        public string RemoteHost { get; set; } = string.Empty;
        public int RemotePort { get; set; }
        public int LocalPort => 30303;
        public bool IsNetworkIdMatched { get; set; } = true;
        public ConnectionDirection Direction => ConnectionDirection.Out;
        public Guid SessionId { get; } = Guid.NewGuid();
        public Node Node => node;
        public DateTime LastPingUtc { get; set; }
        public DateTime LastPongUtc { get; set; }
        public IPingSender PingSender { get; set; } = null!;

        public void ReceiveMessage(ZeroPacket zeroPacket) { }
        public void EnableSnappy() { }
        public void AddSupportedCapability(Capability capability) { }
        public bool HasAvailableCapability(Capability capability) => true;
        public bool HasAgreedCapability(Capability capability) => true;
        public void AddProtocolHandler(IProtocolHandler handler) { }
        public bool TryGetProtocolHandler(string protocolCode, out IProtocolHandler handler)
        {
            handler = null!;
            return false;
        }
        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender) { }
        public void InitiateDisconnect(DisconnectReason disconnectReason, string details) =>
            throw new AssertionException($"peer {node.Id} disconnected: {disconnectReason} {details}");
        public void MarkDisconnected(DisconnectReason disconnectReason, DisconnectType disconnectType, string details) { }
        public void Handshake(PublicKey handshakeRemoteNodeId) { }
        public void StartTrackingSession() { }
        public void Dispose() { }

        public event EventHandler<DisconnectEventArgs> Disconnecting { add { } remove { } }
        public event EventHandler<DisconnectEventArgs> Disconnected { add { } remove { } }
        public event EventHandler<EventArgs> Initialized { add { } remove { } }
        public event EventHandler<EventArgs> HandshakeComplete { add { } remove { } }
        public event EventHandler<PeerEventArgs> MsgReceived { add { } remove { } }
        public event EventHandler<PeerEventArgs> MsgDelivered { add { } remove { } }
    }
}
