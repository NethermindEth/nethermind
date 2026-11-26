// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Sockets;

/// <summary>
/// Connects two nodes over loopback TCP, each on a chosen RLPx transport, so the socket transport is checked against
/// DotNetty on the wire: the handshake, Hello and Snappy, requests in both directions, and a disconnect.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class RlpxTransportInteropTests
{
    private const int BlockCount = 4;
    private const int TransactionsPerBlock = 20;
    private const int TransactionDataSize = 512;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [TestCase(RlpxTransport.DotNetty, RlpxTransport.Sockets)]
    [TestCase(RlpxTransport.Sockets, RlpxTransport.DotNetty)]
    [TestCase(RlpxTransport.Sockets, RlpxTransport.Sockets)]
    public async Task Nodes_exchange_messages_and_deliver_a_disconnect_reason(RlpxTransport serverTransport, RlpxTransport clientTransport)
    {
        using CancellationTokenSource cancellation = new(Timeout);
        IContainer server = CreateNode(TestItem.PrivateKeyA, serverTransport);
        IContainer client = CreateNode(TestItem.PrivateKeyB, clientTransport);
        try
        {
            await StartNetwork(server);
            Hash256[] blockHashes = StoreChain(server);
            await StartNetwork(client);

            TaskCompletionSource<ISession> clientSessionCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Resolve<IRlpxHost>().SessionCreated += (_, args) => clientSessionCreated.TrySetResult(args.Session);
            TaskCompletionSource<DisconnectEventArgs> serverDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Resolve<IRlpxHost>().SessionDisconnected += (_, _, args) => serverDisconnected.TrySetResult(args);

            IEnode serverEnode = server.Resolve<IEnode>();
            Node serverNode = new(serverEnode.PublicKey, new IPEndPoint(serverEnode.HostIp, serverEnode.Port));
            Assert.That(await client.Resolve<IRlpxHost>().ConnectAsync(serverNode, cancellation.Token), Is.True);

            ISyncPeer serverPeer = await WaitForPeer(client.Resolve<ISyncPeerPool>(), cancellation.Token);
            using OwnedBlockBodies bodies = await serverPeer.GetBlockBodies(blockHashes, cancellation.Token);
            Assert.That(bodies.Bodies.Select(static body => body?.Transactions.Length), Is.All.EqualTo(TransactionsPerBlock));

            ISession clientSession = await clientSessionCreated.Task.WaitAsync(cancellation.Token);
            clientSession.InitiateDisconnect(DisconnectReason.TooManyPeers, "interop test");
            DisconnectEventArgs disconnect = await serverDisconnected.Task.WaitAsync(cancellation.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(disconnect.DisconnectType, Is.EqualTo(DisconnectType.Remote));
                Assert.That(disconnect.Details, Is.EqualTo("message"), "the peer's Disconnect message must arrive before the connection closes");
            }
        }
        finally
        {
            foreach (IContainer node in (IContainer[])[client, server])
            {
                await node.Resolve<IRlpxHost>().Shutdown();
                await node.DisposeAsync();
            }
        }
    }

    private static IContainer CreateNode(PrivateKey nodeKey, RlpxTransport transport)
    {
        ConfigProvider configProvider = new(
            new NetworkConfig { P2PPort = GetAvailablePort(), FilterPeersByRecentIp = false, RlpxTransport = transport },
            new SyncConfig());

        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.WithBlobGasUsed(0).TestObject
        };

        return new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(chainSpec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(nodeKey, null) { InMemoryNetwork = false })
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance))
            .Build();
    }

    private static async Task StartNetwork(IContainer node)
    {
        await node.Resolve<PseudoNethermindRunner>().StartBlockProcessing(CancellationToken.None);
        node.Resolve<IProtocolsManager>();
        await node.Resolve<IRlpxHost>().Init();
        node.Resolve<ISyncPeerPool>().Start();
    }

    /// <summary>Stores blocks whose bodies span several frames, without processing them.</summary>
    private static Hash256[] StoreChain(IContainer node)
    {
        IBlockTree blockTree = node.Resolve<IBlockTree>();
        IEthereumEcdsa ecdsa = node.Resolve<IEthereumEcdsa>();
        Random random = new(42);
        Hash256[] hashes = new Hash256[BlockCount];
        Block parent = blockTree.Head!;
        ulong nonce = 0;
        for (int i = 0; i < BlockCount; i++)
        {
            Transaction[] transactions = new Transaction[TransactionsPerBlock];
            for (int j = 0; j < TransactionsPerBlock; j++)
            {
                byte[] data = new byte[TransactionDataSize];
                random.NextBytes(data);
                transactions[j] = Build.A.Transaction.WithNonce(nonce++).WithData(data).SignedAndResolved(ecdsa, TestItem.PrivateKeyC).TestObject;
            }

            Block block = Build.A.Block.WithParent(parent).WithTransactions(transactions).TestObject;
            Assert.That(blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None), Is.EqualTo(AddBlockResult.Added));
            hashes[i] = block.Hash!;
            parent = block;
        }

        return hashes;
    }

    private static async Task<ISyncPeer> WaitForPeer(ISyncPeerPool syncPeerPool, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (syncPeerPool.AllPeers.FirstOrDefault() is { } peer)
            {
                return peer.SyncPeer;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static int GetAvailablePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
