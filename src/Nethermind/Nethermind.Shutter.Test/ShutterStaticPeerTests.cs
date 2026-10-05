// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO.Abstractions;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.KeyStore.Config;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Shutter.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

public class ShutterStaticPeerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    private const string Bootnode = "/ip4/127.0.0.1/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW";

    [Test]
    public void Invalid_bootnodes_prevent_startup(
        [Values("not-a-multiaddress", "/ip4/127.0.0.1/tcp/9222", "/ip4/127.0.0.1/udp/9222")] string invalid,
        [Values] bool mixed)
    {
        ShutterConfig config = StartupConfig(mixed ? [Bootnode, invalid] : [invalid]);
        IShutterApi api = Substitute.For<IShutterApi>();
        RunShutterP2P step = new(config, api, Substitute.For<IProcessExitSource>(), LimboLogs.Instance);

        ShutterPlugin.ShutterLoadingException exception = Assert.Throws<ShutterPlugin.ShutterLoadingException>(
            () => step.Execute(CancellationToken.None))!;
        Assert.That(exception.InnerException?.Message, Does.Contain("BootnodeP2PAddresses"));
        api.DidNotReceive().StartP2P(Arg.Any<IEnumerable<Multiaddress>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Valid_bootnodes_start_with_the_process_exit_token([Values] bool empty)
    {
        ShutterConfig config = StartupConfig(empty ? [] : [Bootnode, Bootnode]);
        IShutterApi api = Substitute.For<IShutterApi>();
        IProcessExitSource exit = Substitute.For<IProcessExitSource>();
        using CancellationTokenSource stop = new();
        exit.Token.Returns(stop.Token);
        RunShutterP2P step = new(config, api, exit, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        await api.Received(1).StartP2P(
            Arg.Is<IEnumerable<Multiaddress>>(addresses => addresses.Select(address => address.ToString())
                .SequenceEqual(config.BootnodeP2PAddresses!)), stop.Token);
    }

    private static ShutterConfig StartupConfig(string[] bootnodes) => new()
    {
        Validator = false,
        SequencerContractAddress = Address.Zero.ToString(),
        ValidatorRegistryContractAddress = Address.Zero.ToString(),
        KeyBroadcastContractAddress = Address.Zero.ToString(),
        KeyperSetManagerContractAddress = Address.Zero.ToString(),
        BootnodeP2PAddresses = bootnodes
    };


    [Test]
    [CancelAfter(60_000)]
    public async Task A_bootnode_is_connected_again_after_its_connection_closes(CancellationToken token)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using ShutterP2P keyper = Create();
        await using ShutterP2P node = Create();
        Task keyperRun = keyper.Start([], static _ => Task.CompletedTask, stop.Token);
        await WaitUntilAsync(() => keyper.PeerForTest.ListenAddresses.Count > 0, "the keyper never listened", token);
        ILocalPeer keyperPeer = keyper.PeerForTest;
        PeerId keyperId = keyperPeer.Identity.PeerId;
        Multiaddress keyperAddress = Multiaddress.Decode($"{keyperPeer.ListenAddresses.First().ToString().Split("/p2p/")[0]}/p2p/{keyperId}");

        Task nodeRun = node.Start([keyperAddress], static _ => Task.CompletedTask, stop.Token);
        await WaitUntilAsync(() => node.RoutingStateForTest.ConnectedPeers.Contains(keyperId), "the node never connected to its bootnode", token);

        LocalPeer nodePeer = (LocalPeer)node.PeerForTest;
        // The static peer check can reconnect within one 200 ms period, faster than a poll sees the peer gone, so a new session is the signal.
        ISession[] closed = nodePeer.Sessions.ToArray<ISession>();
        foreach (ShutterP2P host in new[] { node, keyper })
        {
            foreach (ISession session in ((LocalPeer)host.PeerForTest).Sessions.ToArray())
            {
                await session.DisconnectAsync();
            }
        }

        await WaitUntilAsync(() => nodePeer.Sessions.Any(session => !closed.Contains(session)) && node.RoutingStateForTest.ConnectedPeers.Contains(keyperId),
            "the bootnode was never connected again", token);

        stop.Cancel();
        await Task.WhenAll(keyperRun, nodeRun);
    }

    [Test]
    [CancelAfter(90_000)]
    public async Task A_host_shut_down_during_a_dial_leaves_no_session_open(CancellationToken token)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using ShutterP2P keyper = Create();
        ShutterP2P node = Create();
        Task keyperRun = keyper.Start([], static _ => Task.CompletedTask, stop.Token);
        await WaitUntilAsync(() => keyper.PeerForTest.ListenAddresses.Count > 0, "the keyper never listened", token);
        LocalPeer keyperPeer = (LocalPeer)keyper.PeerForTest;
        Multiaddress keyperAddress = Multiaddress.Decode($"{keyperPeer.ListenAddresses.First().ToString().Split("/p2p/")[0]}/p2p/{keyperPeer.Identity.PeerId}");
        // The dialer's own session: closed as soon as it is added after disposal, so the remote may never list it.
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ((LocalPeer)node.PeerForTest).Sessions.CollectionChanged += (_, change) =>
        {
            if (change.Action == NotifyCollectionChangedAction.Add) reached.TrySetResult();
        };
        Task<ISession> dial = node.PeerForTest.DialAsync(keyperAddress, token);

        bool inFlight = !dial.IsCompleted;
        await node.DisposeAsync();
        Assert.That(inFlight, Is.True, "fixture: shutdown began before the dial finished");
        try
        {
            await dial;
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
        }

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        // Dial timeout is 15 s; a remote losing the connection mid-handshake can take another 30 s to drop it.
        using (CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            bounded.CancelAfter(TimeSpan.FromSeconds(45));
            while (keyperPeer.Sessions.Count > 0 && !bounded.IsCancellationRequested)
            {
                await Task.Delay(50, CancellationToken.None);
            }
        }

        Assert.That(keyperPeer.Sessions, Is.Empty, "the dial finished after shutdown and its session stayed open");
        await stop.CancelAsync();
        await keyperRun;
    }

    // The router's own redial is off, so only the static peer check can connect a bootnode again.
    private static ShutterP2P Create()
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback));
        return new ShutterP2P(new ShutterConfig { P2PPort = 0 }, LimboLogs.Instance, Substitute.For<IFileSystem>(), new KeyStoreConfig(), ipResolver,
            staticPeerCheckInterval: TimeSpan.FromMilliseconds(200), configureGossip: static settings => settings.ReconnectionPeriod = Timeout.Infinite);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure, CancellationToken token)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(Bound);
        while (!condition())
        {
            if (bounded.IsCancellationRequested)
            {
                Assert.Fail(failure);
            }

            await Task.Delay(50, CancellationToken.None);
        }
    }
}
