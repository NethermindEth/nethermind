// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures devp2p request round trips between two nodes over loopback TCP: the request and the response each pass
/// through serialization, Snappy, RLPx framing and encryption, the socket, and the receiving protocol handler.
/// </summary>
/// <remarks>
/// Both nodes run in this process on production modules, so the allocations cover both ends of the exchange. Requests
/// go out one at a time because a peer keeps one request of each message type in flight. The server's chain is stored
/// without being processed and synchronization never starts, so nothing else crosses the connection while measuring.
/// </remarks>
[MemoryDiagnoser]
public class RlpxRoundTripBenchmarks
{
    private const int BlockCount = 16;
    private const int TransactionsPerBlock = 100;
    private const int TransactionDataSize = 512;
    private const int CodeCount = 32;
    private const int CodeSize = 24 * 1024;
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);
    private static readonly ReceiptMessageDecoder ReceiptDecoder = new();

    private readonly Hash256[] _blockHashes = new Hash256[BlockCount];
    private readonly ValueHash256[] _codeHashes = new ValueHash256[CodeCount];
    private IContainer _server = null!;
    private IContainer _client = null!;
    private ISyncPeer _serverPeer = null!;
    private ISnapSyncPeer _serverSnapPeer = null!;

    [GlobalSetup]
    public void GlobalSetup() => SetUpAsync().GetAwaiter().GetResult();

    [GlobalCleanup]
    public void GlobalCleanup() => CleanUpAsync().GetAwaiter().GetResult();

    /// <summary>A single header each way, so the fixed per-message cost dominates.</summary>
    [Benchmark]
    public async Task<int> SingleHeader()
    {
        using IOwnedReadOnlyList<BlockHeader> headers = await _serverPeer.GetBlockHeaders(1, 1, 0, CancellationToken.None);
        return headers.Count;
    }

    [Benchmark]
    public async Task<int> BlockBodies()
    {
        using OwnedBlockBodies bodies = await _serverPeer.GetBlockBodies(_blockHashes, CancellationToken.None);
        return bodies.Bodies.Length;
    }

    [Benchmark]
    public async Task<int> Receipts()
    {
        using IOwnedReadOnlyList<TxReceipt[]> receipts = await _serverPeer.GetReceipts(_blockHashes, CancellationToken.None);
        return receipts.Count;
    }

    /// <summary>Snap byte codes, which the response message keeps in the received buffer instead of copying.</summary>
    [Benchmark]
    public async Task<int> ByteCodes()
    {
        using IByteArrayList codes = await _serverSnapPeer.GetByteCodes(_codeHashes, CancellationToken.None);
        return codes.Count;
    }

    private async Task SetUpAsync()
    {
        using CancellationTokenSource cancellation = new(SetupTimeout);

        _server = CreateNode(TestItem.PrivateKeyA);
        await StartNetwork(_server, cancellation.Token);
        StoreChain(_server);
        StoreCodes(_server);

        _client = CreateNode(TestItem.PrivateKeyB);
        await StartNetwork(_client, cancellation.Token);

        IEnode serverEnode = _server.Resolve<IEnode>();
        Node serverNode = new(serverEnode.PublicKey, new IPEndPoint(serverEnode.HostIp, serverEnode.Port));
        if (!await _client.Resolve<IRlpxHost>().ConnectAsync(serverNode, cancellation.Token))
        {
            throw new InvalidOperationException($"Failed to connect to {serverNode:s}");
        }

        (_serverPeer, _serverSnapPeer) = await WaitForServerPeer(_client.Resolve<ISyncPeerPool>(), cancellation.Token);
        await WarmUpRequestSizes(cancellation.Token);
    }

    /// <remarks>
    /// The hosts are shut down explicitly: <see cref="StartNetwork"/> starts them directly, so disposing the
    /// containers does not stop them. The cleanup tolerates a setup that failed before creating both nodes.
    /// </remarks>
    private async Task CleanUpAsync()
    {
        foreach (IContainer node in (IContainer[])[_client, _server])
        {
            if (node is null) continue;

            await node.Resolve<IRlpxHost>().Shutdown();
            await node.DisposeAsync();
        }
    }

    /// <summary>
    /// Repeats the requests until every response is complete, so that measurements never see a partial one.
    /// </summary>
    /// <remarks>
    /// The client starts with small request sizes and grows them after each fast response, so the first responses
    /// cover only part of what was asked for.
    /// </remarks>
    private async Task WarmUpRequestSizes(CancellationToken cancellationToken)
    {
        const int MaxAttempts = 50;
        string incomplete = string.Empty;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            using OwnedBlockBodies bodies = await _serverPeer.GetBlockBodies(_blockHashes, cancellationToken);
            using IOwnedReadOnlyList<TxReceipt[]> receipts = await _serverPeer.GetReceipts(_blockHashes, cancellationToken);
            using IByteArrayList codes = await _serverSnapPeer.GetByteCodes(_codeHashes, cancellationToken);
            int completeBodies = bodies.Bodies.Count(static body => body?.Transactions.Length == TransactionsPerBlock);
            int completeReceipts = receipts.Count(static block => block?.Length == TransactionsPerBlock);
            if (completeBodies == BlockCount && completeReceipts == BlockCount && codes.Count == CodeCount)
            {
                return;
            }

            incomplete = $"{completeBodies}/{BlockCount} bodies, {completeReceipts}/{BlockCount} receipts " +
                $"(of {receipts.Count}, first {receipts.FirstOrDefault()?.Length.ToString() ?? "null"}), {codes.Count}/{CodeCount} codes";
        }

        throw new InvalidOperationException($"Responses are still incomplete after {MaxAttempts} requests: {incomplete}.");
    }

    private static IContainer CreateNode(PrivateKey nodeKey)
    {
        ConfigProvider configProvider = new(
            new NetworkConfig { P2PPort = GetAvailablePort(), FilterPeersByRecentIp = false },
            // Both ends advertise snap, so the session agrees on it.
            new SyncConfig { SnapServingEnabled = true });

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

    /// <remarks>
    /// Unlike <see cref="PseudoNethermindRunner.StartNetwork"/>, this does not start the synchronizer, so neither node
    /// requests anything on its own beyond refreshing the head of a newly added peer.
    /// </remarks>
    private static async Task StartNetwork(IContainer node, CancellationToken cancellationToken)
    {
        await node.Resolve<PseudoNethermindRunner>().StartBlockProcessing(cancellationToken);
        node.Resolve<IProtocolsManager>();
        await node.Resolve<IRlpxHost>().Init();
        node.Resolve<ISyncPeerPool>().Start();
    }

    private void StoreChain(IContainer node)
    {
        IBlockTree blockTree = node.Resolve<IBlockTree>();
        IReceiptStorage receiptStorage = node.Resolve<IReceiptStorage>();
        IEthereumEcdsa ecdsa = node.Resolve<IEthereumEcdsa>();
        Random random = new(42);
        Block parent = blockTree.Head!;
        ulong nonce = 0;

        for (int i = 0; i < BlockCount; i++)
        {
            Transaction[] transactions = new Transaction[TransactionsPerBlock];
            TxReceipt[] receipts = new TxReceipt[TransactionsPerBlock];
            for (int j = 0; j < TransactionsPerBlock; j++)
            {
                byte[] data = new byte[TransactionDataSize];
                random.NextBytes(data);
                transactions[j] = Build.A.Transaction
                    .WithNonce(nonce++)
                    .WithData(data)
                    .SignedAndResolved(ecdsa, TestItem.PrivateKeyC)
                    .TestObject;
                receipts[j] = Build.A.Receipt
                    .WithLogs(Build.A.LogEntry
                        .WithAddress(TestItem.AddressD)
                        .WithTopics(TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC)
                        .WithData(data[..64])
                        .TestObject)
                    .TestObject;
            }

            Block block = Build.A.Block
                .WithParent(parent)
                .WithTransactions(transactions)
                // Receipt storage serves nothing for a block whose receipts root is the empty trie.
                .WithReceiptsRoot(ReceiptTrie.CalculateRoot(Osaka.Instance, receipts, ReceiptDecoder))
                .TestObject;
            if (blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None) != AddBlockResult.Added)
            {
                throw new InvalidOperationException($"Failed to store block {block.ToString(Block.Format.Short)}");
            }

            receiptStorage.Insert(block, receipts);
            _blockHashes[i] = block.Hash!;
            parent = block;
        }
    }

    private void StoreCodes(IContainer node)
    {
        IDb codeDb = node.ResolveKeyed<IDb>(DbNames.Code);
        Random random = new(43);
        for (int i = 0; i < CodeCount; i++)
        {
            byte[] code = new byte[CodeSize];
            random.NextBytes(code);
            _codeHashes[i] = ValueKeccak.Compute(code);
            codeDb[_codeHashes[i].Bytes] = code;
        }
    }

    private static async Task<(ISyncPeer, ISnapSyncPeer)> WaitForServerPeer(ISyncPeerPool syncPeerPool, CancellationToken cancellationToken)
    {
        while (true)
        {
            PeerInfo peer = syncPeerPool.AllPeers.FirstOrDefault();
            if (peer is not null && peer.SyncPeer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer snapPeer))
            {
                return (peer.SyncPeer, snapPeer);
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
