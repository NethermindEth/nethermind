// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Merge.Plugin;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Test.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

[TestFixture(SynchronizerType.Fast)]
[TestFixture(SynchronizerType.Full)]
[TestFixture(SynchronizerType.Eth2MergeFull)]
[TestFixture(SynchronizerType.Eth2MergeFast)]
[TestFixture(SynchronizerType.Eth2MergeFastWithoutTTD)]
[TestFixture(SynchronizerType.Eth2MergeFullWithoutTTD)]
[Parallelizable(ParallelScope.Self)]
public class SynchronizerTests
{
    private const int SyncBatchSizeMax = 128;

    private readonly SynchronizerType _synchronizerType;

    public SynchronizerTests(SynchronizerType synchronizerType)
    {
        _synchronizerType = synchronizerType;
    }

    private static readonly Block _genesisBlock = Build.A.Block
        .Genesis
        .WithDifficulty(100000)
        .WithTotalDifficulty((UInt256)100000).TestObject;

    private class SyncPeerMock : BaseSyncPeerMock
    {
        public override string Name => "Mock";

        private readonly bool _causeTimeoutOnInit;
        private readonly bool _causeTimeoutOnBlocks;
        private readonly bool _causeTimeoutOnHeaders;
        private List<Block> Blocks { get; } = new();

        public Block HeadBlock => Blocks.Last();

        public BlockHeader HeadHeader => HeadBlock.Header;

        public SyncPeerMock(
            string peerName,
            bool causeTimeoutOnInit = false,
            bool causeTimeoutOnBlocks = false,
            bool causeTimeoutOnHeaders = false)
        {
            _causeTimeoutOnInit = causeTimeoutOnInit;
            _causeTimeoutOnBlocks = causeTimeoutOnBlocks;
            _causeTimeoutOnHeaders = causeTimeoutOnHeaders;
            Blocks.Add(_genesisBlock);
            UpdateHead();
            ClientId = peerName;
        }

        private void UpdateHead()
        {
            HeadHash = HeadBlock.Hash!;
            HeadNumber = HeadBlock.Number;
            TotalDifficulty = HeadBlock.TotalDifficulty ?? 0;
        }

        public override Node Node { get; set; } = new Node(Build.A.PrivateKey.TestObject.PublicKey, "127.0.0.1", 1234);

        public override void Disconnect(DisconnectReason reason, string details)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        public override Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        {
            if (_causeTimeoutOnBlocks)
            {
                return Task.FromException<OwnedBlockBodies>(new TimeoutException());
            }

            BlockBody[] result = new BlockBody[blockHashes.Count];
            for (int i = 0; i < blockHashes.Count; i++)
            {
                foreach (Block block in Blocks)
                {
                    if (block.Hash == blockHashes[i])
                    {
                        result[i] = new BlockBody(block.Transactions, block.Uncles);
                    }
                }
            }

            return Task.FromResult(new OwnedBlockBodies(result));
        }

        public override Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            if (_causeTimeoutOnHeaders)
            {
                return Task.FromException<IOwnedReadOnlyList<BlockHeader>?>(new TimeoutException());
            }

            int filled = 0;
            bool started = false;
            ArrayPoolList<BlockHeader> result = new ArrayPoolList<BlockHeader>(maxBlocks, maxBlocks);
            foreach (Block block in Blocks)
            {
                if (block.Number == number)
                {
                    started = true;
                }

                if (started)
                {
                    result[filled++] = block.Header;
                }

                if (filled >= maxBlocks)
                {
                    break;
                }
            }

            return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(result);
        }

        public override async Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            if (_causeTimeoutOnInit)
            {
                Console.WriteLine("RESPONDING TO GET HEAD BLOCK HEADER WITH EXCEPTION");
                await Task.FromException<BlockHeader>(new TimeoutException());
            }

            BlockHeader header;
            try
            {
                header = Blocks.Last().Header;
            }
            catch (Exception)
            {
                Console.WriteLine("RESPONDING TO GET HEAD BLOCK HEADER EXCEPTION");
                throw;
            }

            Console.WriteLine($"RESPONDING TO GET HEAD BLOCK HEADER WITH RESULT {header.Number}");
            return header;
        }

        public override void NotifyOfNewBlock(Block block, SendBlockMode mode)
        {
            if (mode == SendBlockMode.FullBlock)
                ReceivedBlocks.Push(block);
        }

        public ConcurrentStack<Block> ReceivedBlocks { get; } = new();

        public event EventHandler? Disconnected;

        public override PublicKey Id => Node.Id;

        public override void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx) { }

        public void AddBlocksUpTo(int i, int branchStart = 0, byte branchIndex = 0)
        {
            Block block = Blocks.Last();
            for (long j = block.Number; j < i; j++)
            {
                block = Build.A.Block.WithDifficulty(1000000).WithParent(block)
                    .WithTotalDifficulty(block.TotalDifficulty + 1000000)
                    .WithExtraData(j < branchStart ? [] : new[] { branchIndex }).TestObject;
                Blocks.Add(block);
            }

            UpdateHead();
        }

        public void AddHighDifficultyBlocksUpTo(int i, int branchStart = 0, byte branchIndex = 0)
        {
            Block block = Blocks.Last();
            for (long j = block.Number; j < i; j++)
            {
                block = Build.A.Block.WithParent(block).WithDifficulty(2000000)
                    .WithTotalDifficulty(block.TotalDifficulty + 2000000)
                    .WithExtraData(j < branchStart ? [] : new[] { branchIndex }).TestObject;
                Blocks.Add(block);
            }

            UpdateHead();
        }

        public override string ToString()
        {
            return $"SyncPeerMock:{ClientId}";
        }
    }

    private WhenImplementation When => new(_synchronizerType);

    private class WhenImplementation
    {
        private readonly SynchronizerType _synchronizerType;

        public WhenImplementation(SynchronizerType synchronizerType)
        {
            _synchronizerType = synchronizerType;
        }

        public SyncingContext Syncing => new(_synchronizerType);
    }

    public class SyncingContext : IAsyncDisposable
    {
        private bool _wasStopped = false;
        public static ConcurrentQueue<SyncingContext> AllInstances { get; } = new();

        private readonly Dictionary<string, ISyncPeer> _peers = new();
        private IBlockTree BlockTree => FromContainer.BlockTree;

        private ISyncServer SyncServer => FromContainer.SyncServer;

        private ISynchronizer Synchronizer => FromContainer.Synchronizer;
        private ISyncPeerPool SyncPeerPool => FromContainer.SyncPeerPool;

        private IContainer Container { get; }

        // Its faster to resolve them all at once.
        private ContainerDependencies FromContainer { get; }
        private record ContainerDependencies(
            IBlockTree BlockTree,
            SyncServer SyncServer,
            ISynchronizer Synchronizer,
            ISyncPeerPool SyncPeerPool
        );

        readonly ILogManager _logManager = LimboLogs.Instance;

        private readonly ILogger _logger;

        public SyncingContext(SynchronizerType synchronizerType)
        {
            ISyncConfig GetSyncConfig() =>
                synchronizerType switch
                {
                    SynchronizerType.Fast => SyncConfig.WithFastSync,
                    SynchronizerType.Full => SyncConfig.WithFullSyncOnly,
                    SynchronizerType.Eth2MergeFastWithoutTTD => SyncConfig.WithFastSync,
                    SynchronizerType.Eth2MergeFullWithoutTTD => SyncConfig.WithFullSyncOnly,
                    SynchronizerType.Eth2MergeFast => SyncConfig.WithFastSync,
                    SynchronizerType.Eth2MergeFull => SyncConfig.WithFullSyncOnly,
                    _ => throw new ArgumentOutOfRangeException(nameof(synchronizerType), synchronizerType, null)
                };

            _logger = _logManager.GetClassLogger();
            ISyncConfig syncConfig = GetSyncConfig();
            IMergeConfig mergeConfig = new MergeConfig();
            IPruningConfig pruningConfig = new PruningConfig()
            {
                Mode = PruningMode.Full, // Memory pruning is slow to start, relatively speaking
            };
            if (WithTTD(synchronizerType))
            {
                mergeConfig.Enabled = true;
                mergeConfig.TerminalTotalDifficulty = UInt256.MaxValue.ToString(CultureInfo.InvariantCulture);
            }

            IConfigProvider configProvider = new ConfigProvider(syncConfig, mergeConfig, pruningConfig);
            ContainerBuilder builder = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<ISpecProvider>(MainnetSpecProvider.Instance)
                .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance)
                .AddSingleton(Substitute.For<IProcessExitSource>())
                .AddSingleton<ISealValidator>(Always.Valid)
                .AddSingleton<IBlockValidator>(Always.Valid)
                .AddSingleton<ContainerDependencies>()
                .AddSingleton(_logManager);

            if (IsMerge(synchronizerType))
            {
                builder.RegisterModule(new TestMergeModule(configProvider));
            }

            Container = builder.Build();
            FromContainer = Container.Resolve<ContainerDependencies>();
            _ = FromContainer.SyncServer; // Need to be created once to register events.
            SyncPeerPool.Start();
            Synchronizer.Start();
            AllInstances.Enqueue(this);
        }

        public SyncingContext BestKnownNumberIs(long number)
        {
            Assert.That(BlockTree.BestKnownNumber, Is.EqualTo(number), "best known number");
            return this;
        }

        public SyncingContext BlockIsKnown()
        {
            Assert.That(BlockTree.IsKnownBlock(_blockHeader.Number, _blockHeader.Hash!), Is.True, "block is known");
            return this;
        }

        private const int DynamicTimeout = 10000;

        public SyncingContext BestSuggestedHeaderIs(BlockHeader header)
        {
            Assert.That(
                () => BlockTree.BestSuggestedHeader,
                Is.EqualTo(header).After(DynamicTimeout, 10), "header");

            _blockHeader = BlockTree.BestSuggestedHeader!;
            return this;
        }

        public SyncingContext BestSuggestedBlockHasNumber(long number)
        {
            _logger.Info($"ASSERTING THAT NUMBER IS {number}");

            Assert.That(
                () => BlockTree.BestSuggestedHeader!.Number,
                Is.EqualTo(number).After(DynamicTimeout, 10), "block number");

            _blockHeader = BlockTree.BestSuggestedHeader!;
            return this;
        }

        public SyncingContext BlockIsSameAsGenesis()
        {
            Assert.That(_blockHeader, Is.SameAs(BlockTree.Genesis), "genesis");
            return this;
        }

        private BlockHeader _blockHeader = null!;

        public SyncingContext Genesis
        {
            get
            {
                _blockHeader = BlockTree.Genesis!;
                return this;
            }
        }

        public SyncingContext WaitUntilInitialized()
        {
            Assert.That(() => SyncPeerPool.AllPeers.All(p => p.IsInitialized), Is.True.After(DynamicTimeout, 1));
            return this;
        }

        public SyncingContext After(Action action)
        {
            action();
            return this;
        }

        public SyncingContext BestSuggested
        {
            get
            {
                _blockHeader = BlockTree.BestSuggestedHeader!;
                return this;
            }
        }

        public SyncingContext AfterProcessingGenesis()
        {
            Block genesis = _genesisBlock;
            BlockTree.SuggestBlock(genesis);
            BlockTree.UpdateMainChain(genesis);
            return this;
        }

        public SyncingContext AfterPeerIsAdded(ISyncPeer syncPeer)
        {
            ISyncPeerPool syncPeerPool = SyncPeerPool;
            ((SyncPeerMock)syncPeer).Disconnected += (_, _) => syncPeerPool.RemovePeer(syncPeer);

            _logger.Info($"PEER ADDED {syncPeer.ClientId}");
            _peers.TryAdd(syncPeer.ClientId, syncPeer);
            SyncPeerPool.AddPeer(syncPeer);
            return this;
        }

        public SyncingContext AfterPeerIsRemoved(ISyncPeer syncPeer)
        {
            _peers.Remove(syncPeer.ClientId);
            SyncPeerPool.RemovePeer(syncPeer);
            return this;
        }

        public SyncingContext AfterNewBlockMessage(Block block, ISyncPeer peer)
        {
            _logger.Info($"NEW BLOCK MESSAGE {block.Number}");
            block.Header.TotalDifficulty = block.Difficulty * (ulong)(block.Number + 1);
            SyncServer.AddNewBlock(block, peer);
            return this;
        }

        public SyncingContext AfterHintBlockMessage(Block block, ISyncPeer peer)
        {
            _logger.Info($"HINT BLOCK MESSAGE {block.Number}");
            SyncServer.HintBlock(block.Hash!, block.Number, peer);
            return this;
        }

        public SyncingContext PeerCountIs(long i)
        {
            Assert.That(SyncPeerPool.AllPeers.Count(), Is.EqualTo(i), "peer count");
            return this;
        }

        public SyncingContext PeerCountEventuallyIs(long i)
        {
            Assert.That(() => SyncPeerPool.AllPeers.Count(), Is.EqualTo(i).After(5000, 10), "peer count");
            return this;
        }

        public async Task StopAsync()
        {
            if (_wasStopped) return;
            _wasStopped = true;
            await Container.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

        public SyncingContext WaitALittle()
        {
            Thread.Sleep(100);
            return this;
        }
    }

    [OneTimeSetUp]
    public void Setup()
    {
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        foreach (SyncingContext syncingContext in SyncingContext.AllInstances)
        {
            await syncingContext.StopAsync();
        }
    }

    [Test, Retry(3)]
    public async Task Init_condition_are_as_expected()
    {
        await When.Syncing
            .AfterProcessingGenesis()
            .BestKnownNumberIs(0)
            .Genesis.BlockIsKnown()
            .BestSuggested.BlockIsSameAsGenesis()
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_sync_with_one_peer_straight()
    {
        SyncPeerMock peerA = new("A");

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggested.BlockIsSameAsGenesis()
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_sync_with_one_peer_straight_and_extend_chain()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(3);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_extend_chain_by_one_on_new_block_message()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .WaitUntilInitialized()
            .After(() => peerA.AddBlocksUpTo(2))
            .AfterNewBlockMessage(peerA.HeadBlock, peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_reorg_on_new_block_message()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(3);

        SyncPeerMock peerB = new("B");
        peerB.AddBlocksUpTo(3);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .AfterPeerIsAdded(peerB)
            .WaitUntilInitialized()
            .After(() => peerB.AddBlocksUpTo(6))
            .AfterNewBlockMessage(peerB.HeadBlock, peerB)
            .BestSuggestedHeaderIs(peerB.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    [Ignore("Not supported for now - still analyzing this scenario")]
    public async Task Can_reorg_on_hint_block_message()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(3);

        SyncPeerMock peerB = new("B");
        peerB.AddBlocksUpTo(3);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .AfterPeerIsAdded(peerB)
            .After(() => peerB.AddBlocksUpTo(6))
            .AfterHintBlockMessage(peerB.HeadBlock, peerB)
            .BestSuggestedHeaderIs(peerB.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_extend_chain_by_one_on_block_hint_message()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .WaitUntilInitialized()
            .After(() => peerA.AddBlocksUpTo(2))
            .AfterHintBlockMessage(peerA.HeadBlock, peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_extend_chain_by_more_than_one_on_new_block_message()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .WaitUntilInitialized()
            .After(() => peerA.AddBlocksUpTo(8))
            .AfterNewBlockMessage(peerA.HeadBlock, peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Will_ignore_new_block_that_is_far_ahead()
    {
        // this test was designed for no sync-timer sync process
        // now it checks something different
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .WaitUntilInitialized()
            .After(() => peerA.AddBlocksUpTo(16))
            .AfterNewBlockMessage(peerA.HeadBlock, peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_sync_when_best_peer_is_timing_out()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(1);

        SyncPeerMock badPeer = new("B", false, false, true);
        badPeer.AddBlocksUpTo(20);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(badPeer)
            .WaitUntilInitialized()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedBlockHasNumber(1)
            .StopAsync();
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public async Task Will_inform_connecting_peer_about_the_alternative_branch_with_same_difficulty()
    {

        if (WithTTD(_synchronizerType)) { return; }
        if (_synchronizerType == SynchronizerType.Fast)
        {
            return;
        }

        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(2);

        SyncPeerMock peerB = new("B");
        peerB.AddBlocksUpTo(2, 0, 1);

        await using SyncingContext syncingContext = When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedBlockHasNumber(2)
            .AfterPeerIsAdded(peerB)
            .WaitUntilInitialized();

        Assert.That(peerA.HeadBlock.Hash, Is.Not.EqualTo(peerB.HeadBlock.Hash));

        Block? peerBNewBlock = null;
        Assert.That(() =>
        {
            bool receivedBlock = peerB.ReceivedBlocks.TryPeek(out peerBNewBlock);
            return receivedBlock && peerBNewBlock!.Hash == peerA.HeadBlock.Hash;
        }, Is.True.After(WaitTime, 1));

        Assert.That(peerA.HeadBlock.Hash, Is.EqualTo(peerBNewBlock?.Header.Hash!));
    }

    [Test]
    public async Task Will_not_add_same_peer_twice()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .AfterPeerIsAdded(peerA)
            .WaitUntilInitialized()
            .PeerCountIs(1)
            .BestSuggestedBlockHasNumber(1)
            .StopAsync();
    }

    [Test]
    public async Task Will_remove_peer_when_init_fails()
    {
        SyncPeerMock peerA = new("A", true, true);
        peerA.AddBlocksUpTo(1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .PeerCountEventuallyIs(0)
            .StopAsync();
    }


    [Test]
    public async Task Can_remove_peers()
    {
        SyncPeerMock peerA = new("A");
        SyncPeerMock peerB = new("B");

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .AfterPeerIsAdded(peerB)
            .PeerCountIs(2)
            .AfterPeerIsRemoved(peerB)
            .PeerCountIs(1)
            .AfterPeerIsRemoved(peerA)
            .PeerCountIs(0)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_reorg_on_add_peer()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(SyncBatchSizeMax);

        SyncPeerMock peerB = new("B");
        peerB.AddBlocksUpTo(SyncBatchSizeMax * 2, 0, 1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .AfterPeerIsAdded(peerB)
            .BestSuggestedHeaderIs(peerB.HeadHeader)
            .StopAsync();
    }

    [Test]
    public async Task Can_reorg_based_on_total_difficulty()
    {
        if (WithTTD(_synchronizerType)) { return; }
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(10);

        SyncPeerMock peerB = new("B");
        peerB.AddHighDifficultyBlocksUpTo(6, 0, 1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .AfterPeerIsAdded(peerB)
            .BestSuggestedHeaderIs(peerB.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    [Ignore("Not supported for now - still analyzing this scenario")]
    public async Task Can_extend_chain_on_hint_block_when_high_difficulty_low_number()
    {

        if (WithTTD(_synchronizerType)) { return; }
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(10);

        SyncPeerMock peerB = new("B");
        peerB.AddHighDifficultyBlocksUpTo(5, 0, 1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .AfterPeerIsAdded(peerB)
            .After(() => peerB.AddHighDifficultyBlocksUpTo(6, 0, 1))
            .AfterHintBlockMessage(peerB.HeadBlock, peerB)
            .BestSuggestedHeaderIs(peerB.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_extend_chain_on_new_block_when_high_difficulty_low_number()
    {

        if (WithTTD(_synchronizerType)) { return; }
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(10);

        SyncPeerMock peerB = new("B");
        peerB.AddHighDifficultyBlocksUpTo(6, 0, 1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .WaitUntilInitialized()
            .AfterPeerIsAdded(peerB)
            .WaitUntilInitialized()
            .After(() => peerB.AddHighDifficultyBlocksUpTo(6, 0, 1))
            .AfterNewBlockMessage(peerB.HeadBlock, peerB)
            .WaitUntilInitialized()
            .BestSuggestedHeaderIs(peerB.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Will_not_reorganize_on_same_chain_length()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(10);

        SyncPeerMock peerB = new("B");
        peerB.AddBlocksUpTo(10, 0, 1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .AfterPeerIsAdded(peerB)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test]
    public async Task Will_not_reorganize_more_than_max_reorg_length()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(PowForwardHeaderProvider.MaxReorganizationLength + 1);

        SyncPeerMock peerB = new("B");
        peerB.AddBlocksUpTo(PowForwardHeaderProvider.MaxReorganizationLength + 2, 0, 1);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .AfterPeerIsAdded(peerB)
            .WaitALittle()
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test, Ignore("travis")]
    public async Task Can_sync_more_than_a_batch()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(SyncBatchSizeMax * 3);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_sync_exactly_one_batch()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(SyncBatchSizeMax);

        await When.Syncing
            .AfterProcessingGenesis()
            .AfterPeerIsAdded(peerA)
            .BestSuggestedHeaderIs(peerA.HeadHeader)
            .StopAsync();
    }

    [Test, Retry(3)]
    public async Task Can_stop()
    {
        SyncPeerMock peerA = new("A");
        peerA.AddBlocksUpTo(SyncBatchSizeMax);

        await When.Syncing
            .StopAsync();
    }

    private const int WaitTime = 1500;

    private static bool IsMerge(SynchronizerType synchronizerType) =>
        synchronizerType switch
        {
            SynchronizerType.Eth2MergeFast or SynchronizerType.Eth2MergeFull
                or SynchronizerType.Eth2MergeFastWithoutTTD or SynchronizerType.Eth2MergeFullWithoutTTD
                => true,
            _ => false
        };

    private static bool WithTTD(SynchronizerType synchronizerType) =>
        synchronizerType switch
        {
            SynchronizerType.Eth2MergeFast or SynchronizerType.Eth2MergeFull => true,
            _ => false
        };

}
