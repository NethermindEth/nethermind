// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>Loopback beacon nodes on one fork, for tests that drive real libp2p sessions between them.</summary>
internal static class PeerSessionNodes
{
    private const ulong AnchorSlot = 13_410_304;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly ConditionalWeakTable<BeaconP2P, YamuxFaultLog> FaultLogs = [];

    public sealed record Node(BeaconP2P P2P, BeaconChainStatusHolder StatusHolder, BeaconChainConfig Config)
    {
        public PeerManager CreatePeerManager() => new(P2P, Config, StatusHolder, LimboLogs.Instance);
    }

    public static StatusMessageV2 Status => new()
    {
        ForkDigest = ForkDigest.Compute(Spec, Spec.GetEpoch(AnchorSlot)),
        FinalizedRoot = Hash256.Zero,
        FinalizedEpoch = Spec.GetEpoch(AnchorSlot),
        HeadRoot = Hash256.Zero,
        HeadSlot = AnchorSlot,
        EarliestAvailableSlot = AnchorSlot,
    };

    /// <param name="served">What the node answers <c>status</c> with; defaults to its own holder.</param>
    /// <param name="privateKey">The node's secp256k1 libp2p key; a fresh one by default.</param>
    public static Node Create(IBeaconChainStatusSource? served = null, byte[]? privateKey = null, Lazy<IBeaconSyncPeerPool>? peerPool = null)
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStatusHolder statusHolder = new(Spec, Timestamper.Default) { CurrentStatus = Status };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        if (privateKey is not null)
        {
            store.PutMetadata("p2pIdentityKey", privateKey);
        }

        BeaconP2P p2p = Watched(logs => new BeaconP2P(config, Spec, store, served ?? statusHolder, new LocalMetadataSource(),
            new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), logs, peerPool: peerPool));
        return new Node(p2p, statusHolder, config);
    }

    /// <summary>Creates a node through <paramref name="create"/> with a log that <see cref="DialAsync"/> reads the pinned yamux's session faults from.</summary>
    public static BeaconP2P Watched(Func<ILogManager, BeaconP2P> create)
    {
        YamuxFaultLog log = new();
        BeaconP2P node = create(log);
        FaultLogs.Add(node, log);
        return node;
    }

    /// <summary>Dials <paramref name="to"/> from <paramref name="from"/> and returns once both nodes finished identify on the session,
    /// three attempts in all while the pinned yamux loses the new session.</summary>
    /// <remarks>Both sides open an identify stream as the session starts, and the pinned yamux keeps its streams in an unsynchronized
    /// dictionary: it can drop one, so that identify times out, or throw, so it closes the session at once. When the dialer drops the
    /// listener's identify stream the dial still succeeds and the listener closes the session later, so the dial waits for both sides.
    /// Only a loss with one of these two marks on either node is dialed again, once both nodes have let go of the lost session.</remarks>
    public static async Task<ISession> DialAsync(BeaconP2P from, BeaconP2P to, CancellationToken token)
    {
        for (int attempts = 1; ; attempts++)
        {
            int losses = YamuxLosses(from) + YamuxLosses(to);
            string lost;
            try
            {
                ISession session = await from.DialPeerAsync(LoopbackAddress(to), token);
                if (await HasIdentifiedSessionAsync(from, to.LocalPeerId!, token) && await HasIdentifiedSessionAsync(to, from.LocalPeerId!, token))
                {
                    return session;
                }

                lost = "a node closed the session before its identify completed";
            }
            catch (Libp2pException e) when (attempts < 3 && YamuxLosses(from) + YamuxLosses(to) > losses)
            {
                lost = e.Message;
            }

            // The session is torn down after its loss is recorded, and a dial with the same identity is refused until then.
            await WaitUntilAsync(() => !HoldsSession(from, to.LocalPeerId!) && !HoldsSession(to, from.LocalPeerId!),
                "the session lost to yamux was not torn down", token);
            if (attempts == 3 || YamuxLosses(from) + YamuxLosses(to) <= losses)
            {
                Assert.Fail($"Dial attempt {attempts} lost its session with no yamux loss on either node: {lost}");
            }

            TestContext.Out.WriteLine($"Dial attempt {attempts} lost its session to the pinned yamux, dialing again: {lost}; {LastYamuxFault(from, to)}");
        }
    }

    /// <summary>Dials <paramref name="server"/> from a plain peer logging to <paramref name="requesterLog"/> and returns once the server
    /// finished identify on the session; throws <see cref="TimeoutException"/> for <see cref="RetryStalledAsync{T}"/> when the pinned yamux
    /// lost the session (see <see cref="DialAsync"/>).</summary>
    public static async Task<ISession> DialFromPlainPeerAsync(ILocalPeer requester, YamuxFaultLog requesterLog, BeaconP2P server, CancellationToken token)
    {
        int losses = YamuxLosses(server) + requesterLog.Faults;
        string lost;
        try
        {
            ISession session = await requester.DialAsync(LoopbackAddress(server), token).WaitAsync(token);
            if (await HasIdentifiedSessionAsync(server, requester.Identity.PeerId, token))
            {
                return session;
            }

            lost = "the server closed the session before its identify completed";
        }
        catch (Libp2pException e) when (YamuxLosses(server) + requesterLog.Faults > losses)
        {
            lost = e.Message;
        }

        await WaitUntilAsync(() => !HoldsSession(server, requester.Identity.PeerId), "the session lost to yamux was not torn down", token);
        if (YamuxLosses(server) + requesterLog.Faults <= losses)
        {
            Assert.Fail($"The session was lost with no yamux loss on either peer: {lost}");
        }

        throw new TimeoutException($"The pinned yamux lost the session: {lost}; {requesterLog.LastFault ?? LastYamuxFault(server)}");
    }

    /// <summary>Whether <paramref name="node"/> holds a session with <paramref name="peerId"/> whose identify completed, waiting for it to end.</summary>
    private static async Task<bool> HasIdentifiedSessionAsync(BeaconP2P node, PeerId peerId, CancellationToken token)
    {
        if (SessionWith(node, peerId) is not { } session)
        {
            return false;
        }

        try
        {
            await node.GetSessionInfoAsync(session, token);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HoldsSession(BeaconP2P node, PeerId peerId) => SessionWith(node, peerId) is not null;

    private static LocalPeer.Session? SessionWith(BeaconP2P node, PeerId peerId)
    {
        if (node.LocalPeerForTest is not { } peer)
        {
            return null;
        }

        // The library adds and removes sessions under a lock on the collection, so an unlocked copy can hold a null slot.
        lock (peer.Sessions)
        {
            return peer.Sessions.FirstOrDefault(session => peerId.Equals(session.State.RemotePeerId));
        }
    }

    /// <summary>Identify exchanges of <paramref name="node"/> that timed out plus sessions its yamux closed on a fault of its stream table.</summary>
    private static int YamuxLosses(BeaconP2P node) => node.IdentifyTimeoutsForTest + (FaultLogs.TryGetValue(node, out YamuxFaultLog? log) ? log.Faults : 0);

    private static string LastYamuxFault(params BeaconP2P[] nodes) =>
        nodes.Select(static node => FaultLogs.TryGetValue(node, out YamuxFaultLog? log) ? log.LastFault : null).LastOrDefault(static fault => fault is not null)
        ?? "an identify exchange timed out";

    public static Multiaddress LoopbackAddress(BeaconP2P node) => Multiaddress.Decode(LoopbackAddressText(node));

    public static string LoopbackAddressText(BeaconP2P node, bool withPeerId = true)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        int peerIdAt = address.IndexOf("/p2p/", StringComparison.Ordinal);
        if (peerIdAt >= 0)
        {
            address = address[..peerIdAt];
        }

        return withPeerId ? $"{address}/p2p/{node.LocalPeerId}" : address;
    }

    /// <summary>Runs <paramref name="attempt"/> again when it stalls, three attempts in all: it throws <see cref="TimeoutException"/> or outlives <paramref name="bound"/>.</summary>
    /// <remarks>The pinned yamux keeps its streams in an unsynchronized dictionary, so a stream opened while the other side opens
    /// one can be dropped and strand that exchange; <paramref name="bound"/> must exceed any delay the test asserts on.</remarks>
    public static async Task<T> RetryStalledAsync<T>(Func<CancellationToken, Task<T>> attempt, CancellationToken token, TimeSpan? bound = null)
    {
        for (int attempts = 1; ; attempts++)
        {
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(bound ?? TimeSpan.FromSeconds(15));
            try
            {
                return await attempt(bounded.Token);
            }
            catch (Exception e) when ((e is TimeoutException || bounded.IsCancellationRequested) && attempts < 3 && !token.IsCancellationRequested)
            {
                TestContext.Out.WriteLine($"Attempt {attempts} failed, retrying: {e.GetType().Name} {e.Message}");
            }
        }
    }

    /// <summary>Throws <see cref="TimeoutException"/> when a session of any node is still waiting on identify or was closed for it.</summary>
    /// <remarks>Identify completes within milliseconds on loopback, so one still pending after a failed wait, or one that ran
    /// out its bound, is a stream the pinned yamux dropped (see <see cref="RetryStalledAsync{T}"/>), not a failure of the code under test.</remarks>
    public static void ThrowIfIdentifyStalled(params BeaconP2P[] nodes)
    {
        foreach (BeaconP2P node in nodes)
        {
            if (node.IdentifyTimeoutsForTest != 0)
            {
                throw new TimeoutException($"{node.IdentifyTimeoutsForTest} identify exchange(s) timed out");
            }

            foreach (LocalPeer.Session session in node.LocalPeerForTest?.Sessions.ToArray() ?? [])
            {
                if (!node.GetSessionInfoAsync(session, CancellationToken.None).IsCompleted)
                {
                    throw new TimeoutException($"Identify with {session.State.RemotePeerId} stalled");
                }
            }
        }
    }

    /// <summary>Calls <see cref="ThrowIfIdentifyStalled"/> when a node does not hold exactly one session.</summary>
    /// <remarks>A peer's own identify can still time out after our side admitted the session, and closing it closes both halves.</remarks>
    public static void ThrowIfIdentifyStalledUnlessOneSessionEach(params BeaconP2P[] nodes)
    {
        if (nodes.Any(static node => node.SessionCountForTest != 1))
        {
            ThrowIfIdentifyStalled(nodes);
        }
    }

    /// <summary>Polls a condition; fails the test instead of hanging once <paramref name="within"/> or the token runs out.</summary>
    /// <param name="stallCheck">Nodes whose stalled identify turns the failure into a <see cref="TimeoutException"/> for <see cref="RetryStalledAsync{T}"/>.</param>
    public static async Task WaitUntilAsync(Func<bool> condition, string failure, CancellationToken token, TimeSpan? within = null, BeaconP2P[]? stallCheck = null)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(within ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (bounded.IsCancellationRequested)
            {
                ThrowIfIdentifyStalled(stallCheck ?? []);
                Assert.Fail(failure);
            }

            await Task.Delay(20, CancellationToken.None);
        }
    }
}

/// <summary>Logs like <see cref="LimboLogs"/>, and also keeps each line in which the pinned yamux reports it closed a session because its stream table threw.</summary>
internal sealed class YamuxFaultLog : ILogManager
{
    private const string FaultMark = "Closed with exception";
    private readonly ConcurrentQueue<string> _faults = new();

    public int Faults => _faults.Count;

    public string? LastFault => _faults.LastOrDefault();

    public ILogger GetClassLogger<T>() => LimboLogs.Instance.GetClassLogger<T>();

    public ILogger GetLogger(string loggerName) =>
        loggerName.EndsWith(nameof(YamuxProtocol), StringComparison.Ordinal) ? new(new Capture(_faults)) : LimboLogs.Instance.GetLogger(loggerName);

    private sealed class Capture(ConcurrentQueue<string> faults) : InterfaceLogger
    {
        public bool IsInfo => true;
        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsTrace => true;
        public bool IsError => true;

        public void Info(string text) => Keep(text);
        public void Warn(string text) => Keep(text);
        public void Debug(string text) => Keep(text);
        public void Trace(string text) => Keep(text);
        public void Error(string text, Exception? ex = null) => Keep(text);

        private void Keep(string text)
        {
            int at = text.IndexOf(FaultMark, StringComparison.Ordinal);
            if (at >= 0)
            {
                // The line goes on with the stack trace; the quoted message is enough to name the fault.
                int open = text.IndexOf('"', at);
                int end = open < 0 ? -1 : text.IndexOf('"', open + 1);
                faults.Enqueue($"yamux closed the session on {(end < 0 ? "an unnamed fault" : text[open..(end + 1)])}");
            }
        }
    }
}

/// <summary>Answers each inbound <c>status</c> request, numbered from 1 in arrival order, through a script that may block or throw.</summary>
/// <remarks>A throw becomes an error chunk; a block holds the answer on the serving thread.</remarks>
internal sealed class ScriptedStatusSource(Func<int, StatusMessageV2> answer) : IBeaconChainStatusSource
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    public StatusMessageV2 CurrentStatus => answer(Interlocked.Increment(ref _requests));

    public Hash256 JustifiedRoot => Hash256.Zero;

    public bool ExecutionInSync => false;
}

/// <summary>A plain libp2p peer on loopback that never identifies its own sessions and answers identify with the given protocol.</summary>
internal sealed class PlainPeer(ServiceProvider services, LocalPeer peer, YamuxFaultLog log) : IAsyncDisposable
{
    public LocalPeer Peer => peer;

    public YamuxFaultLog Log => log;

    public Multiaddress Address => peer.ListenAddresses.First();

    /// <param name="statusSource">When given, the peer also answers <c>status</c> v2 from it, and nothing else of the eth2 protocols.</param>
    public static async Task<PlainPeer> StartAsync(Func<IProtocolStackSettings, IdentifyProtocol> identify, CancellationToken token, IBeaconChainStatusSource? statusSource = null,
        bool pingOnDial = false, Identity? identity = null)
    {
        ServiceCollection collection = new();
        YamuxFaultLog log = new();
        collection.AddSingleton(BeaconP2P.CreateLibp2pLoggerFactory(log));
        collection.AddSingleton(sp => identify(sp.GetRequiredService<IProtocolStackSettings>()));
        if (statusSource is not null)
        {
            collection.AddSingleton(new StatusProtocolV2(statusSource));
        }

        ServiceProvider services = collection
            .AddLibp2p(builder => statusSource is null ? builder : builder.AddProtocol<StatusProtocolV2>())
            .BuildServiceProvider();
        // Building the factory is what fills the stack settings the peer runs on.
        services.GetRequiredService<IPeerFactory>();
        PlainPeer plain = new(services, new NonIdentifyingPeer(services.GetRequiredService<PeerStore>(), services.GetRequiredService<IProtocolStackSettings>(), log, pingOnDial, identity), log);
        await plain.Peer.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], token);
        return plain;
    }

    public async ValueTask DisposeAsync()
    {
        await peer.DisposeAsync();
        await services.DisposeAsync();
    }

    private sealed class NonIdentifyingPeer(PeerStore peerStore, IProtocolStackSettings settings, YamuxFaultLog log, bool pingOnDial, Identity? identity)
        : LocalPeer(identity ?? new Identity(privateKey: null, KeyType.Secp256K1), peerStore, settings, loggerFactory: BeaconP2P.CreateLibp2pLoggerFactory(log))
    {
        protected override Task ConnectedTo(ISession session, bool isDialer) => pingOnDial && isDialer
            ? session.DialAsync<PingProtocol>()
            : Task.CompletedTask;
    }
}
