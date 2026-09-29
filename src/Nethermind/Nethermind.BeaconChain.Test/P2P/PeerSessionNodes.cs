// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
using Nethermind.Libp2p.Protocols;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>Loopback beacon nodes on one fork, for tests that drive real libp2p sessions between them.</summary>
internal static class PeerSessionNodes
{
    private const ulong AnchorSlot = 13_410_304;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

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

        BeaconP2P p2p = new(config, Spec, store, served ?? statusHolder, new LocalMetadataSource(),
            new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance, peerPool: peerPool);
        return new Node(p2p, statusHolder, config);
    }

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
internal sealed class PlainPeer(ServiceProvider services, LocalPeer peer) : IAsyncDisposable
{
    public LocalPeer Peer => peer;

    public Multiaddress Address => peer.ListenAddresses.First();

    /// <param name="statusSource">When given, the peer also answers <c>status</c> v2 from it, and nothing else of the eth2 protocols.</param>
    public static async Task<PlainPeer> StartAsync(Func<IProtocolStackSettings, IdentifyProtocol> identify, CancellationToken token, IBeaconChainStatusSource? statusSource = null)
    {
        ServiceCollection collection = new();
        collection.AddSingleton(sp => identify(sp.GetRequiredService<IProtocolStackSettings>()));
        if (statusSource is not null)
        {
            collection.AddSingleton(new StatusProtocolV2(statusSource));
        }

        ServiceProvider services = collection
            .AddLibp2p(builder => statusSource is null ? builder : builder.AddAppLayerProtocol<StatusProtocolV2>())
            .BuildServiceProvider();
        // Building the factory is what fills the stack settings the peer runs on.
        services.GetRequiredService<IPeerFactory>();
        PlainPeer plain = new(services, new NonIdentifyingPeer(services.GetRequiredService<PeerStore>(), services.GetRequiredService<IProtocolStackSettings>()));
        await plain.Peer.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], token);
        return plain;
    }

    public async ValueTask DisposeAsync()
    {
        await peer.DisposeAsync();
        await services.DisposeAsync();
    }

    private sealed class NonIdentifyingPeer(PeerStore peerStore, IProtocolStackSettings settings)
        : LocalPeer(new Identity(privateKey: null, KeyType.Secp256K1), peerStore, settings)
    {
        protected override Task ConnectedTo(ISession session, bool isDialer) => Task.CompletedTask;
    }
}
