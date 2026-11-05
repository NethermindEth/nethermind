// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using System.Threading;
using Autofac;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Follower;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;

namespace Nethermind.Eez.Test;

public class EezSnapBootstrapTests
{
    private IContainer _container = null!;
    private IBlockTree _blocks = null!;
    private EezSnapBootstrap _bootstrap = null!;
    private EezConfig _config = null!;
    private SyncConfig _sync = null!;
    private EezChainRules _rules = null!;
    private IEezL1Api _l1 = null!;
    private IEezSequencerApi _source = null!;
    private Block _pivot = null!;
    private EezL1Log[] _settlements = [];
    private static readonly Address Registry = TestItem.AddressA;

    [SetUp]
    public void SetUp()
    {
        _l1 = Substitute.For<IEezL1Api>();
        _source = Substitute.For<IEezSequencerApi>();
        _config = new EezConfig
        {
            SnapBootstrapEnabled = true,
            FollowerEnabled = true,
            RegistryAddress = Registry.ToString(),
            RollupId = 1,
            RegistryDeployBlock = 2,
            L1ChainId = 10200,
            SequencerRpcUrl = "http://127.0.0.1:8545"
        };
        _sync = new SyncConfig { SynchronizationEnabled = false, FastSync = true };
        _rules = EezChainRules.NativeRollupX(new RollupXSchedule(1, 1,
            new RollupXL1Reference(10200, 1, TestItem.KeccakA, 100, TestItem.KeccakB), 100), TestItem.AddressB, NativeSystemTxFormat.ContractV7);
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Genesis = Build.A.Block.WithTimestamp(100).WithBlobGasUsed(0).TestObject,
            Allocations = new()
            {
                [TestItem.AddressA] = new ChainSpecAllocation
                {
                    Balance = 10,
                    Code = [0x00],
                    Storage = new() { [UInt256.One] = [0x01] },
                },
            },
        };
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(_sync, new FlatDbConfig { Enabled = false }), chainSpec))
            .AddSingleton<IEezConfig>(_config)
            .AddSingleton(_rules)
            .AddSingleton(_l1)
            .AddSingleton(_source)
            .AddSingleton<ResumePointFinder, IEezL1Api, IBlockTree>((l1, blocks) =>
                new ResumePointFinder(l1, blocks, Registry, 1, 2, 100, _rules.Schedule))
            .AddSingleton<EezSnapBootstrap>()
            .Build();
        _blocks = _container.Resolve<IBlockTree>();
        IMainProcessingContext processing = _container.Resolve<IMainProcessingContext>();
        processing.BlockProcessingQueue.Start();
        processing.GenesisLoader.Load();
        _pivot = Build.A.Block.WithParent(_blocks.Genesis!).WithTimestamp(101)
            .WithStateRoot(_blocks.Genesis!.StateRoot!).WithWithdrawals([]).TestObject;
        _sync.PivotNumber = _pivot.Number;
        _sync.PivotHash = _pivot.Hash!.ToString();
        _blocks.SyncPivot = (_pivot.Number, _pivot.Hash!);
        Store(_pivot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_sync.PivotNumber, Is.EqualTo(_pivot.Number), "the configured pivot must remain readable with synchronization disabled");
            Assert.That(_sync.PivotHash, Is.EqualTo(_pivot.Hash!.ToString()));
        }
        _source.GetRawBlock(_pivot.Hash!, Arg.Any<CancellationToken>()).Returns(Rlp.Encode(_pivot).Bytes);
        _l1.GetChainId(Arg.Any<CancellationToken>()).Returns(10200UL);
        _l1.GetBlockByNumber(1, Arg.Any<CancellationToken>()).Returns(new EezL1Block
        { Number = 1, Hash = TestItem.KeccakA, Timestamp = 100, MixHash = TestItem.KeccakB });
        _l1.GetBlockByNumber(2, Arg.Any<CancellationToken>()).Returns(new EezL1Block
        { Number = 2, Hash = TestItem.KeccakC, Timestamp = 101, MixHash = TestItem.KeccakB });
        _l1.GetFinalizedBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 2, Hash = TestItem.KeccakC });
        _settlements = [Settlement(_pivot.Hash!, 0)];
        _l1.GetLogs(Registry, L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(1),
            Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(_ => _settlements);
        _bootstrap = _container.Resolve<EezSnapBootstrap>();
    }

    [TearDown]
    public void TearDown() => _container.Dispose();

    [Test]
    public async Task Prepare_CompleteSnapshot_InstallsAndRepairsCheckpoint([Values] bool interrupted)
    {
        if (interrupted)
            Assert.That(_blocks.TryUpdateMainChain(_pivot.Header, true, true, _pivot), Is.True);

        await _bootstrap.Prepare(CancellationToken.None);
        await _bootstrap.Prepare(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_blocks.Head!.Hash, Is.EqualTo(_pivot.Hash));
            Assert.That(_blocks.SafeHash, Is.EqualTo(_pivot.Hash));
            Assert.That(_blocks.FinalizedHash, Is.EqualTo(_pivot.Hash));
            Assert.That(_blocks.GetInfo(_pivot.Number, _pivot.Hash!).Info!.WasProcessed, Is.True);
        }
    }

    [Test]
    public async Task Prepare_ProcessedDescendant_PreservesHeads()
    {
        await _bootstrap.Prepare(CancellationToken.None);
        Block next = Next(_pivot.Header, 102);
        Store(next);
        Assert.That(_blocks.TryUpdateMainChain(next.Header, true, true, next), Is.True);
        _blocks.ForkChoiceUpdated(next.Hash, next.Hash);
        _blocks.SyncPivot = (next.Number, next.Hash!);

        await _bootstrap.Prepare(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_blocks.Head!.Hash, Is.EqualTo(next.Hash));
            Assert.That(_blocks.SafeHash, Is.EqualTo(next.Hash));
            Assert.That(_blocks.FinalizedHash, Is.EqualTo(next.Hash));
        }
    }

    [Test]
    public async Task Prepare_DurableHeadBehindForkchoice_PreservesStoredHeads([Values] bool atPivot)
    {
        await _bootstrap.Prepare(CancellationToken.None);
        Block durable = atPivot ? _pivot : Next(_pivot.Header, 102);
        if (!atPivot)
        {
            Store(durable);
            Assert.That(_blocks.TryUpdateMainChain(durable.Header, true, true, durable), Is.True);
        }
        Block finalized = Build.A.Block.WithParent(durable.Header).WithTimestamp(durable.Timestamp + 1)
            .WithStateRoot(TestItem.KeccakD).WithWithdrawals([]).TestObject;
        Store(finalized);
        Assert.That(_blocks.TryUpdateMainChain(finalized.Header, true, true, finalized), Is.True);
        Block safe = Build.A.Block.WithParent(finalized.Header).WithTimestamp(finalized.Timestamp + 1)
            .WithStateRoot(TestItem.KeccakE).WithWithdrawals([]).TestObject;
        Store(safe);
        Assert.That(_blocks.TryUpdateMainChain(safe.Header, true, true, safe), Is.True);
        _blocks.ForkChoiceUpdated(finalized.Hash, safe.Hash);

        using ILifetimeScope restarted = RestartAt(durable);
        IBlockTree blocks = restarted.Resolve<IBlockTree>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Head!.Hash, Is.EqualTo(durable.Hash));
            Assert.That(blocks.SafeHash, Is.EqualTo(safe.Hash));
            Assert.That(blocks.FinalizedHash, Is.EqualTo(finalized.Hash));
            Assert.That(restarted.Resolve<IStateReader>().HasStateForBlock(safe.Header), Is.False);
        }

        await restarted.Resolve<EezSnapBootstrap>().Prepare(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Head!.Hash, Is.EqualTo(durable.Hash));
            Assert.That(blocks.SafeHash, Is.EqualTo(safe.Hash));
            Assert.That(blocks.FinalizedHash, Is.EqualTo(finalized.Hash));
        }
    }

    [Test]
    public async Task Prepare_InvalidForkchoiceAheadOfDurableHead_RefusesWithoutRewriting([Values] ForkchoiceCorruption corruption)
    {
        await _bootstrap.Prepare(CancellationToken.None);
        Block durable = Next(_pivot.Header, 102);
        Store(durable);
        Assert.That(_blocks.TryUpdateMainChain(durable.Header, true, true, durable), Is.True);
        BlockHeader parent = durable.Header;
        if (corruption == ForkchoiceCorruption.Intermediate)
        {
            Block intermediate = Next(parent, 103);
            Store(intermediate);
            Assert.That(_blocks.TryUpdateMainChain(intermediate.Header, false, false, intermediate), Is.True);
            parent = intermediate.Header;
        }
        Block ahead = Next(parent, parent.Timestamp + 1);
        Store(ahead);
        Assert.That(_blocks.TryUpdateMainChain(ahead.Header, corruption != ForkchoiceCorruption.Unprocessed,
            corruption != ForkchoiceCorruption.Unprocessed, ahead), Is.True);
        if (corruption == ForkchoiceCorruption.Noncanonical)
            Assert.That(_blocks.TryUpdateMainChain(durable.Header, true, true, durable), Is.True);
        Hash256 safe = corruption == ForkchoiceCorruption.Order ? durable.Hash! : ahead.Hash!;
        _blocks.ForkChoiceUpdated(ahead.Hash, safe);

        using ILifetimeScope restarted = RestartAt(durable);
        IBlockTree blocks = restarted.Resolve<IBlockTree>();
        await Assert.ThrowsAsync<EezFollowerException>(() => restarted.Resolve<EezSnapBootstrap>().Prepare(CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Head!.Hash, Is.EqualTo(durable.Hash));
            Assert.That(blocks.SafeHash, Is.EqualTo(safe));
            Assert.That(blocks.FinalizedHash, Is.EqualTo(ahead.Hash));
        }
    }

    [Test]
    public async Task Prepare_PartialForkchoiceAtPivot_RefusesWithoutRepair([Values] bool onlySafe)
    {
        await _bootstrap.Prepare(CancellationToken.None);
        Hash256? finalized = onlySafe ? null : _pivot.Hash;
        Hash256? safe = onlySafe ? _pivot.Hash : null;
        _blocks.ForkChoiceUpdated(finalized, safe);

        await Assert.ThrowsAsync<EezFollowerException>(() => _bootstrap.Prepare(CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_blocks.Head!.Hash, Is.EqualTo(_pivot.Hash));
            Assert.That(_blocks.SafeHash, Is.EqualTo(safe));
            Assert.That(_blocks.FinalizedHash, Is.EqualTo(finalized));
        }
    }

    [Test]
    public async Task Prepare_InvalidDescendant_RefusesWithoutRewinding([Values] DescendantCorruption corruption)
    {
        await _bootstrap.Prepare(CancellationToken.None);
        BlockHeader parent = _pivot.Header;
        if (corruption == DescendantCorruption.Ancestry)
        {
            Block fork = Build.A.Block.WithParent(_blocks.Genesis!).WithTimestamp(101)
                .WithExtraData([0x77]).WithStateRoot(Keccak.EmptyTreeHash).WithWithdrawals([]).TestObject;
            Store(fork);
            parent = fork.Header;
        }
        Block next = Build.A.Block.WithParent(parent).WithTimestamp(102)
            .WithStateRoot(corruption == DescendantCorruption.State ? TestItem.KeccakD : Keccak.EmptyTreeHash)
            .WithWithdrawals([]).TestObject;
        Store(next);
        Assert.That(_blocks.TryUpdateMainChain(next.Header, true, true, next), Is.True);
        Hash256 forkchoice = corruption == DescendantCorruption.Finality ? _blocks.Genesis!.Hash! : next.Hash!;
        _blocks.ForkChoiceUpdated(forkchoice, forkchoice);

        await Assert.ThrowsAsync<EezFollowerException>(() => _bootstrap.Prepare(CancellationToken.None));
        Assert.That(_blocks.Head!.Hash, Is.EqualTo(next.Hash), "refusing invalid restart data must not rewrite the chain");
    }

    [Test]
    public async Task Prepare_UnfinalizedOrSupersededCommitment_LeavesHeadAtGenesis([Values] bool superseded)
    {
        if (superseded) _settlements = [_settlements[0], Settlement(TestItem.KeccakD, 1)];
        else _l1.GetFinalizedBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 1, Hash = TestItem.KeccakA });

        await Assert.ThrowsAsync<EezFollowerException>(() => _bootstrap.Prepare(CancellationToken.None));
        Assert.That(_blocks.Head!.Number, Is.Zero, "a snapshot needs the last rollup commitment of a finalized L1 block");
    }

    [Test]
    public async Task Prepare_WrongL1Identity_LeavesHeadAtGenesis([Values] bool wrongReference)
    {
        if (wrongReference) _l1.GetBlockByNumber(1, Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 1, Hash = TestItem.KeccakD });
        else _l1.GetChainId(Arg.Any<CancellationToken>()).Returns(1UL);

        await Assert.ThrowsAsync<EezFollowerException>(() => _bootstrap.Prepare(CancellationToken.None));
        Assert.That(_blocks.Head!.Number, Is.Zero);
    }

    [Test]
    public async Task Prepare_ReorganizedFinalizedAnchor_LeavesHeadAtGenesis()
    {
        _l1.GetBlockByNumber(2, Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 2, Hash = TestItem.KeccakD, Timestamp = 101, MixHash = TestItem.KeccakB });

        await Assert.ThrowsAsync<EezFollowerException>(() => _bootstrap.Prepare(CancellationToken.None));
        Assert.That(_blocks.Head!.Number, Is.Zero);
    }

    [Test]
    public async Task Prepare_DifferentSourceBody_LeavesHeadAtGenesis()
    {
        _sync.SynchronizationEnabled = true;
        Block authorized = Next(_blocks.Genesis!, 101);
        _sync.PivotHash = authorized.Hash!.ToString();
        _blocks.SyncPivot = (authorized.Number, authorized.Hash!);
        _settlements = [Settlement(authorized.Hash!, 0)];
        Block wrong = new(authorized.Header, new BlockBody([Build.A.Transaction.SignedAndResolved().TestObject], [], []));
        _source.GetRawBlock(authorized.Hash!, Arg.Any<CancellationToken>()).Returns(Rlp.Encode(wrong).Bytes);

        await Assert.ThrowsAsync<EezFollowerException>(() => _bootstrap.Prepare(CancellationToken.None));
        Assert.That(_blocks.Head!.Number, Is.Zero);
    }

    [Test]
    public async Task Prepare_MissingBytecode_LeavesHeadAtGenesis()
    {
        IDb code = _container.Resolve<IDbProvider>().CodeDb;
        code[Keccak.Compute(new byte[] { 0x00 }).Bytes] = null;

        await Assert.ThrowsAsync<EezFollowerException>(() => _bootstrap.Prepare(CancellationToken.None));
        Assert.That(_blocks.Head!.Number, Is.Zero, "a root with missing code cannot become a usable checkpoint");
    }

    [Test]
    public async Task Prepare_MissingPivotState_LeavesHeadAtGenesis()
    {
        Block missing = Build.A.Block.WithParent(_blocks.Genesis!).WithTimestamp(101)
            .WithStateRoot(TestItem.KeccakD).WithWithdrawals([]).TestObject;
        Store(missing);
        _sync.PivotNumber = missing.Number;
        _sync.PivotHash = missing.Hash!.ToString();
        _blocks.SyncPivot = (missing.Number, missing.Hash!);
        _source.GetRawBlock(missing.Hash!, Arg.Any<CancellationToken>()).Returns(Rlp.Encode(missing).Bytes);
        _settlements = [Settlement(missing.Hash!, 0)];

        await Assert.ThrowsAsync<EezFollowerException>(() => _bootstrap.Prepare(CancellationToken.None));
        Assert.That(_blocks.Head!.Number, Is.Zero);
    }

    [Test]
    public void EnsureSnapBootstrapConfig_UnsafeDownload_RefusesStartup([Values] bool processing)
    {
        _sync.SynchronizationEnabled = true;
        _sync.FastSync = true;
        _sync.SnapSync = true;
        _sync.StaticSnapPivot = true;
        InitConfig init = new() { ProcessingEnabled = processing, PeerManagerEnabled = true };
        JsonRpcConfig rpc = new() { Enabled = !processing };

        Assert.That(() => EezPlugin.EnsureSnapBootstrapConfig(_config, _rules, _sync, init, rpc),
            Throws.TypeOf<InvalidConfigurationException>(), "neither processing nor RPC can run during the download");
    }

    [Test]
    public void EnsureSnapBootstrapConfig_NativeBootstrapDisabledByDefault_LeavesOtherChainsUnchanged() =>
        Assert.That(new EezConfig().SnapBootstrapEnabled, Is.False);

    private ILifetimeScope RestartAt(Block durable) => _container.BeginLifetimeScope(builder => builder
        .AddSingleton<IStateBoundary>(new TestStateBoundary { BestPersistedState = durable.Number })
        .AddSingleton<IBlockTree, BlockTree>()
        .AddSingleton<EezSnapBootstrap>());

    public enum ForkchoiceCorruption
    {
        Unprocessed,
        Intermediate,
        Noncanonical,
        Order,
    }

    private void Store(Block block)
    {
        block.Header.TotalDifficulty = UInt256.Zero;
        Assert.That(_blocks.Insert(block, BlockTreeInsertBlockOptions.SaveHeader,
            BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded), Is.EqualTo(AddBlockResult.Added));
    }

    private static Block Next(BlockHeader parent, ulong timestamp) => Build.A.Block.WithParent(parent)
        .WithTimestamp(timestamp).WithStateRoot(Keccak.EmptyTreeHash).WithWithdrawals([]).TestObject;

    public enum DescendantCorruption
    {
        State,
        Finality,
        Ancestry,
    }

    private static EezL1Log Settlement(Hash256 commitment, ulong index) => new()
    {
        Address = Registry,
        Topics = [L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(1)],
        BlockNumber = 2,
        BlockHash = TestItem.KeccakC,
        TransactionIndex = index,
        Data = [.. commitment.Bytes, .. new byte[32]],
    };
}
