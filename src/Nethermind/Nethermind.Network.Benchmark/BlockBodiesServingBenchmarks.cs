// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.History;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Test;
using Nethermind.TxPool;
using NSubstitute;
using Testably.Abstractions;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Serving GetBlockBodies from a RocksDB block store for blocks older than the block tree's head cache, then
/// serializing the reply, as a peer syncing bodies asks for them.
/// </summary>
[MemoryDiagnoser]
[MaxIterationCount(30)]
public class BlockBodiesServingBenchmarks
{
    private const int RequestedBlocks = 64;
    private const int NewerEmptyBlocks = 200;
    private const int WithdrawalsPerBlock = 16;

    private TempPath _directory = null!;
    private IDb _blocksDb = null!;
    private SyncServer _server = null!;
    private BodiesServingHandler _handler = null!;
    private GetBlockBodiesMessage _request = null!;
    private readonly BlockBodiesMessageSerializer _serializer = new();
    private byte[] _output = null!;

    [Params(30, 150)]
    public int TransactionsPerBlock { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            _blocksDb = new RocksDbFactory(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                    new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path)
                .CreateDb(new DbSettings(DbNames.Blocks, DbNames.Blocks));
        }

        TestSpecProvider specProvider = new(Byzantium.Instance);
        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        BlockStore blockStore = new(_blocksDb);
        BlockTree blockTree = Build.A.BlockTree().WithSpecProvider(specProvider).WithBlockStore(blockStore).OfChainLength(1).TestObject;

        Random random = new(42);
        Hash256[] hashes = new Hash256[RequestedBlocks];
        Block parent = blockTree.Head!;
        ulong nonce = 0;
        for (int number = 0; number < RequestedBlocks + NewerEmptyBlocks; number++)
        {
            bool served = number < RequestedBlocks;
            Transaction[] transactions = new Transaction[served ? TransactionsPerBlock : 0];
            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = CreateTransaction(random, ecdsa, nonce++);
            }

            Block block = Build.A.Block
                .WithParent(parent)
                .WithTransactions(transactions)
                .WithWithdrawals(served ? WithdrawalsPerBlock : 0)
                .TestObject;
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, true, false, block);
            if (served) hashes[number] = block.Hash!;
            parent = block;
        }

        _blocksDb.Flush();
        IClearableCache blockCache = blockStore;
        blockCache.ClearCache();

        _server = new SyncServer(
            Substitute.For<IWorldStateManager>(),
            new MemDb(),
            blockTree,
            Substitute.For<IReceiptFinder>(),
            Substitute.For<IBlockAccessListStore>(),
            Always.Valid,
            Always.Valid,
            Substitute.For<ISyncPeerPool>(),
            StaticSelector.Full,
            new TestSyncConfig(),
            Policy.FullGossip,
            Substitute.For<IHistoryPruner>(),
            specProvider,
            LimboLogs.Instance);
        _handler = new BodiesServingHandler(_server);
        _request = new GetBlockBodiesMessage(hashes);
        _output = new byte[32 * 1024 * 1024];

        CheckReply(hashes, blockTree);
        blockCache.ClearCache();
    }

    private void CheckReply(Hash256[] hashes, BlockTree blockTree)
    {
        int length = Serve();
        using BlockBodiesMessage decoded = _serializer.Deserialize(_output.AsSpan(0, length));
        BlockBody[] bodies = decoded.Bodies!.Bodies;
        if (bodies.Length == 0) throw new InvalidOperationException("No bodies served.");
        for (int i = 0; i < bodies.Length; i++)
        {
            Block expected = blockTree.FindBlock(hashes[i], BlockTreeLookupOptions.TotalDifficultyNotNeeded)!;
            if (bodies[i].Transactions.Length != expected.Transactions.Length
                || bodies[i].Withdrawals?.Length != WithdrawalsPerBlock
                || bodies[i].Transactions[^1].Hash != expected.Transactions[^1].Hash)
            {
                throw new InvalidOperationException($"Body {i} differs from the stored block.");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _request.Dispose();
        _handler.Dispose();
        _server.Dispose();
        _blocksDb.Dispose();
        _directory.Dispose();
    }

    [Benchmark]
    public int Serve()
    {
        using BlockBodiesMessage response = _handler.Serve(_request).Result;
        int length = _serializer.GetLength(response);
        return _serializer.Serialize(_output.AsSpan(0, length), response);
    }

    private static Transaction CreateTransaction(Random random, EthereumEcdsa ecdsa, ulong nonce)
    {
        byte[] data = new byte[random.Next(10) < 3 ? 0 : random.Next(4, 400)];
        random.NextBytes(data);
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithNonce(nonce)
            .WithTo(TestItem.Addresses[random.Next(TestItem.Addresses.Length)])
            .WithValue((UInt256)random.Next())
            .WithData(data)
            .WithGasLimit(100_000);

        builder = (nonce % 10) switch
        {
            < 3 => builder.WithGasPrice(30),
            3 => builder.WithType(TxType.AccessList).WithGasPrice(30).WithAccessList(
                new AccessList.Builder().AddAddress(TestItem.AddressB).AddStorage(1).AddStorage(2).Build()),
            9 => builder.WithType(TxType.EIP1559).WithMaxFeePerGas(40).WithMaxPriorityFeePerGas(2)
                .WithShardBlobTxTypeAndFields(2, isMempoolTx: false),
            _ => builder.WithType(TxType.EIP1559).WithMaxFeePerGas(40).WithMaxPriorityFeePerGas(2),
        };

        return builder.Signed(ecdsa, TestItem.PrivateKeyA, true).TestObject;
    }

    private static ISession CreateSession()
    {
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Loopback, 30303)));
        return session;
    }

    private sealed class BodiesServingHandler(ISyncServer syncServer) : Eth69ProtocolHandler(
        CreateSession(),
        Substitute.For<IMessageSerializationService>(),
        new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
        syncServer,
        Substitute.For<IBackgroundTaskScheduler>(),
        Substitute.For<ITxPool>(),
        Substitute.For<IGossipPolicy>(),
        Substitute.For<IForkInfo>(),
        LimboLogs.Instance,
        Substitute.For<ITxPoolConfig>(),
        Substitute.For<IChainHeadSpecProvider>())
    {
        public Task<BlockBodiesMessage> Serve(GetBlockBodiesMessage request) => FulfillBlockBodiesRequest(request, CancellationToken.None);
    }
}
