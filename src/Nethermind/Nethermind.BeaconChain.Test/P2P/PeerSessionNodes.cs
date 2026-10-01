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

    /// <summary>Dials <paramref name="to"/> from <paramref name="from"/> once and returns when both nodes finished identify on the session.</summary>
    /// <remarks>Both sides open an identify stream as the session starts, so the dial also proves the multiplexer keeps both streams.</remarks>
    public static async Task<ISession> DialAsync(BeaconP2P from, BeaconP2P to, CancellationToken token)
    {
        ISession session = await from.DialPeerAsync(LoopbackAddress(to), token);
        if (!await HasIdentifiedSessionAsync(from, to.LocalPeerId!, token) || !await HasIdentifiedSessionAsync(to, from.LocalPeerId!, token))
        {
            Assert.Fail("a node closed the session before its identify completed");
        }

        return session;
    }

    /// <summary>Dials <paramref name="server"/> from a plain peer once and returns when the server finished identify on the session.</summary>
    public static async Task<ISession> DialFromPlainPeerAsync(ILocalPeer requester, BeaconP2P server, CancellationToken token)
    {
        ISession session = await requester.DialAsync(LoopbackAddress(server), token).WaitAsync(token);
        if (!await HasIdentifiedSessionAsync(server, requester.Identity.PeerId, token))
        {
            Assert.Fail("the server closed the session before its identify completed");
        }

        return session;
    }

    /// <summary>Dials <paramref name="to"/>, which refuses the session, from <paramref name="from"/>; the dial's own outcome is not checked.</summary>
    /// <remarks>A dial ends only after the dialer's own identify, so the refusing node can already have closed the session and failed the dial.</remarks>
    public static async Task DialToBeRefusedAsync(BeaconP2P from, BeaconP2P to, CancellationToken token)
    {
        try
        {
            await from.DialPeerAsync(LoopbackAddress(to), token);
        }
        catch (PeerConnectionException)
        {
        }
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

    /// <summary>Polls a condition; fails the test instead of hanging once <paramref name="within"/> or the token runs out.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string failure, CancellationToken token, TimeSpan? within = null)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(within ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (bounded.IsCancellationRequested)
            {
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
    public static async Task<PlainPeer> StartAsync(Func<IProtocolStackSettings, IdentifyProtocol> identify, CancellationToken token, IBeaconChainStatusSource? statusSource = null,
        bool pingOnDial = false, Identity? identity = null)
    {
        ServiceCollection collection = new();
        collection.AddSingleton(BeaconP2P.CreateLibp2pLoggerFactory(LimboLogs.Instance));
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
        PlainPeer plain = new(services, new NonIdentifyingPeer(services.GetRequiredService<PeerStore>(), services.GetRequiredService<IProtocolStackSettings>(), pingOnDial, identity));
        await plain.Peer.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], token);
        return plain;
    }

    public async ValueTask DisposeAsync()
    {
        await peer.DisposeAsync();
        await services.DisposeAsync();
    }

    private sealed class NonIdentifyingPeer(PeerStore peerStore, IProtocolStackSettings settings, bool pingOnDial, Identity? identity)
        : LocalPeer(identity ?? new Identity(privateKey: null, KeyType.Secp256K1), peerStore, settings, loggerFactory: BeaconP2P.CreateLibp2pLoggerFactory(LimboLogs.Instance))
    {
        protected override Task ConnectedTo(ISession session, bool isDialer) => pingOnDial && isDialer
            ? session.DialAsync<PingProtocol>()
            : Task.CompletedTask;
    }
}
