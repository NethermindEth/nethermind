// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;
using Autofac;
using Nethermind.Config;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers.AllocationStrategies;


namespace Nethermind.Synchronization.Test;

public partial class ForwardHeaderProviderTests
{
    private const int SyncBatchSizeMax = 128;

    [TestCase(1L, 0, 64, 0, 1)]
    [TestCase(1L, 32, 64, 0, 0)]
    [TestCase(2L, 0, 64, 0, 2)]
    [TestCase(2L, 32, 64, 0, 0)]
    [TestCase(32L, 0, 64, 0, 32)]
    [TestCase(32L, 32, 64, 0, 0)]
    [TestCase(32L, 16, 64, 0, 16)]
    [TestCase(32L, 0, 16, 0, 15)]
    [TestCase(3L, 0, 64, 0, 3)]
    [TestCase(3L, 32, 64, 0, 0)]
    [TestCase(SyncBatchSizeMax * 8, 0, 64, 0, 63)]
    [TestCase(SyncBatchSizeMax * 8, 0, 64, 0, 63)]
    [TestCase(SyncBatchSizeMax * 8, 32, 64, 0, 63)]
    [TestCase(SyncBatchSizeMax * 8, 32, 64, 0, 63)]
    public async Task Happy_path(long headNumber, int skipLastN, int maxHeader, int expectedStartNumber, int expectedEndNumber)
    {
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeer = new(chainLength, false, Response.AllCorrect);
        PeerInfo peerInfo = new(syncPeer);

        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        ctx.ConfigureBestPeer(syncPeer);
        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        int maxNHeader = Math.Min(maxHeader, peerInfo!.MaxHeadersPerRequest());

        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(skipLastN, maxNHeader, CancellationToken.None);
        headers?[0]?.Number.Should().Be(expectedStartNumber);
        headers?[^1]?.Number.Should().Be(expectedEndNumber);
    }

    [Test]
    public async Task Ancestor_lookup_simple()
    {
        IBlockTree instance = CachedBlockTreeBuilder.OfLength(1024);
        await using IContainer node = CreateNode(builder =>
        {
            builder.AddSingleton<IBlockTree>(instance);
        });
        Context ctx = node.Resolve<Context>();
        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        Response blockResponseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2048 + 1, false, blockResponseOptions);
        ctx.ConfigureBestPeer(syncPeer);

        Block block1024 = Build.A.Block.WithParent(ctx.BlockTree.Head!).WithDifficulty(ctx.BlockTree.Head!.Difficulty + 1).TestObject;
        Block block1025 = Build.A.Block.WithParent(block1024).WithDifficulty(block1024.Difficulty + 1).TestObject;
        Block block1026 = Build.A.Block.WithParent(block1025).WithDifficulty(block1025.Difficulty + 1).TestObject;
        ctx.BlockTree.SuggestBlock(block1024);
        ctx.BlockTree.SuggestBlock(block1025);
        ctx.BlockTree.SuggestBlock(block1026);

        for (int i = 0; i < 1023; i++)
        {
            Assert.That(syncPeer.BlockTree.FindBlock(i, BlockTreeLookupOptions.None)!.Hash, Is.EqualTo(ctx.BlockTree.FindBlock(i, BlockTreeLookupOptions.None)!.Hash), i.ToString());
        }

        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        headers?[0]?.Number.Should().Be(1019);
        headers?[^1]?.Number.Should().Be(1146);
    }

    [Test]
    public async Task Ancestor_lookup_with_sync_pivot()
    {
        SyncPeerMock syncPeer = new(1024, false, Response.AllCorrect);
        int pivotNumber = 500;
        BlockHeader syncPivot = syncPeer.BlockTree.FindHeader(pivotNumber, BlockTreeLookupOptions.None)!;

        await using IContainer node = CreateNode(builder =>
        {
        }, new ConfigProvider(new SyncConfig()
        {
            PivotNumber = syncPivot.Number.ToString(),
            PivotHash = syncPivot.Hash!.ToString(),
        }));

        // Simulate fast header adding the pivot.
        Context ctx = node.Resolve<Context>();
        ctx.BlockTree.Insert(syncPivot).Should().Be(AddBlockResult.Added);
        ctx.ConfigureBestPeer(syncPeer);
        syncPeer.HeadNumber = 700;

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);

        headers?[0]?.Number.Should().Be(pivotNumber);
    }

    [Test]
    public async Task Ancestor_failure_blocks()
    {
        await using IContainer node = CreateNode(builder =>
        {
            builder.AddSingleton<IBlockTree>(CachedBlockTreeBuilder.OfLength(2048 + 1));
        });
        Context ctx = node.Resolve<Context>();
        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        Response responseOptions = Response.AllCorrect;
        SyncPeerMock syncPeer = new(2072 + 1, true, responseOptions);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        (await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)).Should().BeNull();
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    [TestCase(33L)]
    [TestCase(65L)]
    public async Task Peer_only_advertise_some_header(long headNumber)
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ctx.ResponseBuilder.BuildHeaderResponse(0, (int)(headNumber + 1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(headNumber);

        ctx.ConfigureBestPeer(peerInfo);

        using IOwnedReadOnlyList<BlockHeader?>? headers = await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None);
        headers?[0]?.Number.Should().Be(0);
        headers?[^1]?.Number.Should().Be(headNumber);
    }

    [Test]
    public async Task Throws_on_inconsistent_batch()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect ^ Response.Consistent));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1024);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        (await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)).Should().BeNull();
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    [Test]
    public async Task Throws_on_invalid_seal()
    {
        await using IContainer node = CreateNode(builder => builder.AddSingleton<ISealValidator>(Always.Invalid));
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        (await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)).Should().BeNull();
        ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed, Arg.Any<string>());
    }

    [Test]
    public async Task Cache_block_headers_unless_peer_changed()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(1000);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        (await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)).Should().NotBeNull();
        (await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)).Should().NotBeNull();

        await syncPeer.Received(1).GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        ISyncPeer newSyncPeer = Substitute.For<ISyncPeer>();
        newSyncPeer.HeadHash.Returns(TestItem.KeccakB);
        newSyncPeer.HeadNumber.Returns(1000);
        newSyncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        newSyncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));
        ctx.ConfigureBestPeer(new PeerInfo(newSyncPeer));

        (await forwardHeader.GetBlockHeaders(0, 128, CancellationToken.None)).Should().NotBeNull();
        await syncPeer.Received(1).GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await newSyncPeer.Received(1).GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private class SlowSealValidator : ISealValidator
    {
        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
        {
            Thread.Sleep(1000);
            return true;
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            Thread.Sleep(1000);
            return true;
        }
    }

    [Test, MaxTime(7000)]
    [Ignore("Fails OneLoggerLogManager Travis only")]
    public async Task Can_cancel_seal_validation()
    {
        await using IContainer node = CreateNode(builder => builder.AddSingleton<ISealValidator>(new SlowSealValidator()));
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildHeaderResponse(ci.ArgAt<long>(0), ci.ArgAt<int>(1), Response.AllCorrect));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ctx.ResponseBuilder.BuildBlocksResponse(ci.ArgAt<IList<Hash256>>(0), Response.AllCorrect));

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.HeadNumber.Returns(1000);

        CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(100);

        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Func<Task> headerTask = () => forwardHeader.GetBlockHeaders(0, 128, cancellation.Token);
        await headerTask.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Validate_always_the_last_seal_and_random_seal_in_the_package()
    {
        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(true);
        await using IContainer node = CreateNode(builder => builder.AddSingleton<ISealValidator>(sealValidator));
        Context ctx = node.Resolve<Context>();

        using IOwnedReadOnlyList<BlockHeader>? blockHeaders = await ctx.ResponseBuilder.BuildHeaderResponse(0, 512, Response.AllCorrect);
        BlockHeader[] blockHeadersCopy = blockHeaders?.ToArray() ?? [];
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TotalDifficulty.Returns(UInt256.MaxValue);
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(blockHeaders);

        PeerInfo peerInfo = new(syncPeer);
        syncPeer.HeadNumber.Returns(511);
        ctx.ConfigureBestPeer(peerInfo);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        using IOwnedReadOnlyList<BlockHeader?>? _ = await forwardHeader.GetBlockHeaders(0, 128, default);

        sealValidator.Received(2).ValidateSeal(Arg.Any<BlockHeader>(), true);
        sealValidator.Received(510).ValidateSeal(Arg.Any<BlockHeader>(), false);
        sealValidator.Received().ValidateSeal(blockHeadersCopy![^1], true);
    }

    private class ThrowingPeer(long number, UInt256? totalDiff, Hash256? headHash = null) : ISyncPeer
    {
        public string Name => "Throwing";
        public string ClientId => "EX peer";
        public Node Node { get; } = null!;
        public string ProtocolCode { get; } = null!;
        public byte ProtocolVersion { get; } = default;
        public Hash256 HeadHash { get; set; } = headHash ?? Keccak.Zero;
        public long HeadNumber { get; set; } = number;
        public UInt256 TotalDifficulty { get; set; } = totalDiff ?? UInt256.MaxValue;
        public bool IsInitialized { get; set; }
        public bool IsPriority { get; set; }

        public void Disconnect(DisconnectReason reason, string details)
        {
            throw new NotImplementedException();
        }

        public Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 blockHash, int maxBlocks, int skip, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            throw new InvalidOperationException();
        }

        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
            throw new NotImplementedException();
        }

        public PublicKey Id => Node.Id;

        public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }
    }

    [Test]
    public async Task Faults_on_get_headers_faulting()
    {
        await using IContainer node = CreateNode();
        Context ctx = node.Resolve<Context>();

        ISyncPeer syncPeer = new ThrowingPeer(1000, UInt256.MaxValue);
        ctx.ConfigureBestPeer(syncPeer);

        IForwardHeaderProvider forwardHeader = ctx.ForwardHeaderProvider;
        Func<Task> headerTask = () => forwardHeader.GetBlockHeaders(0, 128, default);
        await headerTask.Should().ThrowAsync<InvalidOperationException>();
    }

    [Flags]
    private enum Response
    {
        Consistent = 1,
        AllCorrect = 7,
        JustFirst = 8,
        AllKnown = 16,
        TimeoutOnFullBatch = 32,
        WithTransactions = 128,
    }

    private IContainer CreateNode(Action<ContainerBuilder>? configurer = null, IConfigProvider? configProvider = null)
    {
        configProvider ??= new ConfigProvider();

        Block genesis = Build.A.Block.Genesis.TestObject;
        ContainerBuilder b = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton<IReceiptStorage, InMemoryReceiptStorage>()
            .AddSingleton<ISealValidator>(Always.Valid)
            .AddSingleton<ISpecProvider>(new MainnetSpecProvider())
            .AddSingleton<IBlockValidator>(Always.Valid)
            .AddSingleton<ISyncPeerPool>(Substitute.For<ISyncPeerPool>())
            .AddSingleton<ResponseBuilder>()
            .AddDecorator<IBlockTree>((ctx, tree) =>
            {
                if (tree.Genesis is null) tree.SuggestBlock(genesis);
                return tree;
            })

            .AddSingleton<Dictionary<long, Hash256>, IBlockTree>((blockTree) => new Dictionary<long, Hash256>()
            {
                {
                    0, blockTree.Genesis!.Hash!
                },
            })
            .AddSingleton<Context>();

        configurer?.Invoke(b);
        return b
            .Build();
    }

    private record Context(
        ResponseBuilder ResponseBuilder,
        IForwardHeaderProvider ForwardHeaderProvider,
        IBlockTree BlockTree,
        ISyncPeerPool PeerPool
    )
    {
        public void ConfigureBestPeer(ISyncPeer syncPeer)
        {
            ConfigureBestPeer(new PeerInfo(syncPeer));
        }

        public void ConfigureBestPeer(PeerInfo peerInfo)
        {
            SyncPeerAllocation peerAllocation = new(peerInfo, AllocationContexts.Blocks, null);

            PeerPool
                .Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(peerAllocation));
        }
    }

    private class SyncPeerMock : ISyncPeer
    {
        private readonly bool _withReceipts;
        private readonly bool _withWithdrawals;
        private readonly BlockHeadersMessageSerializer _headersSerializer = new();
        private readonly BlockBodiesMessageSerializer _bodiesSerializer = new();
        private readonly ReceiptsMessageSerializer _receiptsSerializer = new(MainnetSpecProvider.Instance);
        private readonly Response _flags;

        public IBlockTree BlockTree { get; private set; } = null!;
        private IReceiptStorage _receiptStorage = new InMemoryReceiptStorage();

        public string Name => "Mock";
        public DisconnectReason? DisconnectReason { get; private set; }

        public SyncPeerMock(long chainLength, bool withReceipts, Response flags, bool withWithdrawals = false)
        {
            _withReceipts = withReceipts;
            _withWithdrawals = withWithdrawals;
            _flags = flags;
            BuildTree(chainLength, withReceipts);
        }

        public SyncPeerMock(BlockTree blockTree, bool withReceipts, Response flags, UInt256 peerTotalDifficulty, bool withWithdrawals = false, IReceiptStorage? receiptStorage = null)
        {
            _withReceipts = withReceipts;
            _receiptStorage = receiptStorage!;
            _withWithdrawals = withWithdrawals;
            _flags = flags;
            BlockTree = blockTree;
            HeadNumber = BlockTree.Head!.Number;
            HeadHash = BlockTree.HeadHash!;
            TotalDifficulty = peerTotalDifficulty;
        }

        private void BuildTree(long chainLength, bool withReceipts)
        {
            _receiptStorage = new InMemoryReceiptStorage();
            BlockTreeBuilder builder = Build.A.BlockTree(MainnetSpecProvider.Instance);
            if (withReceipts)
            {
                builder = builder.WithTransactions(_receiptStorage);
            }

            builder = builder.OfChainLength((int)chainLength, 0, 0, _withWithdrawals);
            BlockTree = builder.TestObject;

            HeadNumber = BlockTree.Head!.Number;
            HeadHash = BlockTree.HeadHash!;
            TotalDifficulty = BlockTree.Head.TotalDifficulty ?? 0;
        }

        public Node Node { get; } = null!;
        public string ClientId { get; } = null!;
        public byte ProtocolVersion { get; } = default;
        public string ProtocolCode { get; } = null!;
        public Hash256 HeadHash { get; set; } = null!;
        public PublicKey Id => Node.Id;
        public long HeadNumber { get; set; }
        public UInt256 TotalDifficulty { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsPriority { get; set; }

        public async Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        {
            BlockBody[] headers = new BlockBody[blockHashes.Count];
            int i = 0;
            foreach (Hash256 blockHash in blockHashes)
            {
                headers[i++] = BlockTree.FindBlock(blockHash, BlockTreeLookupOptions.None)!.Body;
            }

            using BlockBodiesMessage message = new(headers);
            byte[] messageSerialized = _bodiesSerializer.Serialize(message);
            return await Task.FromResult(_bodiesSerializer.Deserialize(messageSerialized).Bodies!);
        }

        public async Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            bool justFirst = _flags.HasFlag(Response.JustFirst);
            bool timeoutOnFullBatch = _flags.HasFlag(Response.TimeoutOnFullBatch);

            if (timeoutOnFullBatch && number == SyncBatchSizeMax)
            {
                throw new TimeoutException();
            }

            BlockHeader[] headers = new BlockHeader[maxBlocks];
            for (int i = 0; i < (justFirst ? 1 : maxBlocks); i++)
            {
                headers[i] = BlockTree.FindHeader(number + i, BlockTreeLookupOptions.None)!;
            }

            using BlockHeadersMessage message = new(headers.ToPooledList());
            byte[] messageSerialized = _headersSerializer.Serialize(message);
            return await Task.FromResult(_headersSerializer.Deserialize(messageSerialized).BlockHeaders);
        }

        public async Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token)
        {
            TxReceipt[][] receipts = new TxReceipt[blockHash.Count][];
            int i = 0;
            foreach (Hash256 keccak in blockHash)
            {
                Block? block = BlockTree.FindBlock(keccak, BlockTreeLookupOptions.None);
                TxReceipt[] blockReceipts = _receiptStorage.Get(block!);
                receipts[i++] = blockReceipts;
            }

            using ReceiptsMessage message = new(receipts.ToPooledList());
            byte[] messageSerialized = _receiptsSerializer.Serialize(message);
            return await Task.FromResult(_receiptsSerializer.Deserialize(messageSerialized).TxReceipts);
        }

        public void Disconnect(DisconnectReason reason, string details)
        {
            DisconnectReason = reason;
        }

        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
            throw new NotImplementedException();
        }

        public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            throw new NotImplementedException();
        }

        public Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }

        public bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
        {
            throw new NotImplementedException();
        }
    }

    private class ResponseBuilder
    {
        private readonly IBlockTree _blockTree;
        private readonly Dictionary<long, Hash256> _testHeaderMapping;

        public ResponseBuilder(IBlockTree blockTree, Dictionary<long, Hash256> testHeaderMapping)
        {
            _blockTree = blockTree;
            _testHeaderMapping = testHeaderMapping;
        }

        public async Task<IOwnedReadOnlyList<BlockHeader>?> BuildHeaderResponse(long startNumber, int number, Response flags)
        {
            bool consistent = flags.HasFlag(Response.Consistent);
            bool justFirst = flags.HasFlag(Response.JustFirst);
            bool allKnown = flags.HasFlag(Response.AllKnown);
            bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);
            bool withTransaction = flags.HasFlag(Response.WithTransactions);

            if (timeoutOnFullBatch && number == SyncBatchSizeMax)
            {
                throw new TimeoutException();
            }

            BlockHeader startBlock = _blockTree.FindHeader(_testHeaderMapping[startNumber], BlockTreeLookupOptions.None)!;
            BlockHeader[] headers = new BlockHeader[number];
            headers[0] = startBlock;
            if (!justFirst)
            {
                for (int i = 1; i < number; i++)
                {
                    Hash256 receiptRoot = i == 1 ? Keccak.EmptyTreeHash : new Hash256("0x9904791428367d3f36f2be68daf170039dd0b3d6b23da00697de816a05fb5cc1");
                    BlockHeaderBuilder blockHeaderBuilder = consistent
                        ? Build.A.BlockHeader.WithReceiptsRoot(receiptRoot).WithParent(headers[i - 1])
                        : Build.A.BlockHeader.WithReceiptsRoot(receiptRoot).WithNumber(headers[i - 1].Number + 1);

                    if (withTransaction)
                    {
                        // We don't know the TX root yet, it should be populated by `BuildBlocksResponse` and `BuildReceiptsResponse`.
                        blockHeaderBuilder.WithTransactionsRoot(Keccak.Compute("something"));
                        blockHeaderBuilder.WithReceiptsRoot(Keccak.Compute("something"));
                    }

                    headers[i] = blockHeaderBuilder.TestObject;

                    if (allKnown)
                    {
                        _blockTree.SuggestHeader(headers[i]);
                    }

                    _testHeaderMapping[startNumber + i] = headers[i].Hash!;
                }
            }

            foreach (BlockHeader header in headers)
            {
                _headers[header.Hash!] = header;
            }

            using BlockHeadersMessage message = new(headers.ToPooledList());
            byte[] messageSerialized = _headersSerializer.Serialize(message);
            return await Task.FromResult(_headersSerializer.Deserialize(messageSerialized).BlockHeaders);
        }

        private readonly BlockHeadersMessageSerializer _headersSerializer = new();
        private readonly BlockBodiesMessageSerializer _bodiesSerializer = new();
        private readonly ReceiptsMessageSerializer _receiptsSerializer = new(MainnetSpecProvider.Instance);
        private readonly Dictionary<Hash256, BlockHeader> _headers = new();
        private readonly Dictionary<Hash256, BlockBody> _bodies = new();

        public async Task<OwnedBlockBodies> BuildBlocksResponse(IList<Hash256> blockHashes, Response flags)
        {
            bool consistent = flags.HasFlag(Response.Consistent);
            bool justFirst = flags.HasFlag(Response.JustFirst);
            bool allKnown = flags.HasFlag(Response.AllKnown);
            bool timeoutOnFullBatch = flags.HasFlag(Response.TimeoutOnFullBatch);
            bool withTransactions = flags.HasFlag(Response.WithTransactions);

            if (timeoutOnFullBatch && blockHashes.Count == SyncBatchSizeMax)
            {
                throw new TimeoutException();
            }

            BlockHeader? startHeader = _blockTree.FindHeader(blockHashes[0], BlockTreeLookupOptions.None);
            startHeader ??= _headers[blockHashes[0]];

            BlockHeader[] blockHeaders = new BlockHeader[blockHashes.Count];
            BlockBody[] blockBodies = new BlockBody[blockHashes.Count];

            Block BuildBlockForHeader(BlockHeader header, int txSeed)
            {
                BlockBuilder blockBuilder = Build.A.Block.WithHeader(header);

                if (withTransactions && header.TxRoot != Keccak.EmptyTreeHash)
                {
                    blockBuilder.WithTransactions(Build.A.Transaction.WithValue(txSeed * 2).SignedAndResolved().TestObject,
                        Build.A.Transaction.WithValue(txSeed * 2 + 1).SignedAndResolved().TestObject);
                }

                return blockBuilder.TestObject;
            }

            blockBodies[0] = BuildBlockForHeader(startHeader, 0).Body;
            blockHeaders[0] = startHeader;

            _bodies[startHeader.Hash!] = blockBodies[0];
            _headers[startHeader.Hash!] = blockHeaders[0];
            if (!justFirst)
            {
                for (int i = 0; i < blockHashes.Count; i++)
                {
                    blockHeaders[i] = consistent
                        ? _headers[blockHashes[i]]
                        : Build.A.BlockHeader.WithNumber(blockHeaders[i - 1].Number + 1).WithHash(blockHashes[i]).TestObject;

                    _testHeaderMapping[startHeader.Number + i] = blockHeaders[i].Hash!;

                    BlockHeader header = consistent
                        ? blockHeaders[i]
                        : blockHeaders[i - 1];

                    Block block = BuildBlockForHeader(header, i);
                    blockBodies[i] = block.Body;
                    _bodies[blockHashes[i]] = blockBodies[i];

                    if (allKnown)
                    {
                        _blockTree.SuggestBlock(block);
                    }
                }
            }

            using BlockBodiesMessage message = new(blockBodies);
            byte[] messageSerialized = _bodiesSerializer.Serialize(message);
            return await Task.FromResult(_bodiesSerializer.Deserialize(messageSerialized).Bodies!);
        }
    }
}
