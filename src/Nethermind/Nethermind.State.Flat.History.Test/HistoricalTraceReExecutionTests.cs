// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

/// <summary>
/// Regression guard for historical <c>trace_*</c>/<c>debug_trace*</c> returning <c>[]</c>: the post-block commit
/// used to resolve trie nodes via the history-backed reader, which throws. Drives the tracer's commit path
/// (<c>Commit</c> → <c>RecalculateStateRoot</c> → <c>CommitTree</c>) against a real trie-less scope below the
/// barrier and asserts the re-executed mutations are observable through the flat overlay.
/// </summary>
[TestFixture]
public class HistoricalTraceReExecutionTests
{
    private const long HistoryBarrier = 100;
    private const long HistoricalBlock = 50;
    private static readonly Address ExistingAddr = new("0x0000000000000000000000000000000000000abc");
    private static readonly UInt256 ExistingSlot = 7;

    private ResourcePool _resourcePool = null!;
    private IProcessExitSource _processExitSource = null!;
    private ITrieNodeCache _trieNodeCache = null!;
    private ISnapshotCompactor _snapshotCompactor = null!;
    private ISnapshotRepository _snapshotRepository = null!;
    private IPersistenceManager _persistenceManager = null!;
    private IBlocksConfig _blocksConfig = null!;
    private CancellationTokenSource _cts = null!;

    private SnapshotableMemColumnsDb<FlatDbColumns> _historyDb = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryReader _historyReader = null!;

    [SetUp]
    public void SetUp()
    {
        _resourcePool = new ResourcePool(new FlatDbConfig { CompactSize = 16 });
        _cts = new CancellationTokenSource();
        _processExitSource = Substitute.For<IProcessExitSource>();
        _processExitSource.Token.Returns(_cts.Token);
        _trieNodeCache = Substitute.For<ITrieNodeCache>();
        _snapshotCompactor = Substitute.For<ISnapshotCompactor>();
        _snapshotRepository = Substitute.For<ISnapshotRepository>();
        _persistenceManager = Substitute.For<IPersistenceManager>();
        _blocksConfig = Substitute.For<IBlocksConfig>();
        _blocksConfig.SecondsPerSlot.Returns(12UL);

        _historyDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _historyReader = new HistoryReader(_historyDb, _historyColumns, LimboLogs.Instance);

        _persistenceManager.GetCurrentPersistedStateId().Returns(new StateId(HistoryBarrier, TestItem.KeccakA));
    }

    [TearDown]
    public void TearDown()
    {
        _cts.Cancel();
        _cts.Dispose();
        _historyDb.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public async Task HistoricalTraceReExecution_OverTrieLessScope_DoesNotThrow_AndAppliesChangesToFlatOverlay()
    {
        // History as of the traced block: an account with a single non-zero slot, finalized at block 5.
        Account existing = new(nonce: 3, balance: 300);
        HistoryColumnsWriter.RecordAccount(_historyColumns, ExistingAddr, 5, existing);
        HistoryColumnsWriter.RecordStorage(_historyColumns, ExistingAddr, ExistingSlot, 5, [0xAA]);
        HistoryColumnsWriter.MarkBlockAvailable(_historyColumns, HistoricalBlock);

        IReleaseSpec spec = MuirGlacier.Instance;
        Address freshAddr = TestItem.AddressB;
        UInt256 freshSlot = 11;
        byte[] freshSlotValue = [0xBE, 0xEF];
        byte[] updatedExistingSlotValue = [0x12, 0x34];

        await using FlatDbManager inner = CreateManager();
        HistoricalFlatDbManager manager = new(
            inner,
            _persistenceManager,
            _historyReader,
            _trieNodeCache,
            _resourcePool,
            enableDetailedMetrics: false);
        using FlatScopeProvider scopeProvider = CreateScopeProvider(manager);
        WorldState worldState = new(scopeProvider, LimboLogs.Instance);

        BlockHeader historicalHeader = Build.A.BlockHeader.WithNumber(HistoricalBlock).WithStateRoot(TestItem.KeccakB).TestObject;

        UInt256 existingBalanceAfter;
        Account? freshReadBack;

        Assert.That(() =>
        {
            using IDisposable scope = worldState.BeginScope(historicalHeader);

            // Reading the historical state from flat history must work — this is what the re-executing tx sees.
            Assert.That(worldState.GetNonce(ExistingAddr), Is.EqualTo((ulong)3));
            Assert.That(worldState.GetBalance(ExistingAddr), Is.EqualTo((UInt256)300));

            // Re-execute the kind of mutations a traced transaction performs: touch an existing account, write a brand
            // new account, and change storage for both. These flow through the same write-batch/commit path that the
            // block processor drives for the tracer.
            worldState.AddToBalance(ExistingAddr, 50, spec);
            worldState.IncrementNonce(ExistingAddr);
            worldState.Set(new StorageCell(ExistingAddr, ExistingSlot), updatedExistingSlotValue);

            worldState.CreateAccount(freshAddr, balance: 7, nonce: 1);
            worldState.Set(new StorageCell(freshAddr, freshSlot), freshSlotValue);

            // Block-processing commit sequence that previously threw on the trie-less scope.
            worldState.Commit(spec);
            worldState.RecalculateStateRoot();
            worldState.CommitTree(HistoricalBlock);

            existingBalanceAfter = worldState.GetBalance(ExistingAddr);
            freshReadBack = worldState.GetAccount(freshAddr);

            using (Assert.EnterMultipleScope())
            {
                // The re-executed changes must be visible through the flat overlay — the state a non-empty trace reports.
                Assert.That(existingBalanceAfter, Is.EqualTo((UInt256)350));
                Assert.That(worldState.GetNonce(ExistingAddr), Is.EqualTo((ulong)4));
                Assert.That(worldState.Get(new StorageCell(ExistingAddr, ExistingSlot)).ToArray(), Is.EqualTo(updatedExistingSlotValue));

                Assert.That(freshReadBack.Balance, Is.EqualTo((UInt256)7));
                Assert.That(freshReadBack.Nonce, Is.EqualTo((ulong)1));
                Assert.That(worldState.Get(new StorageCell(freshAddr, freshSlot)).ToArray(), Is.EqualTo(freshSlotValue));

                // The historical root is known up-front and must be retained (no trie traversal recomputed it).
                Assert.That(worldState.StateRoot, Is.EqualTo(TestItem.KeccakB));
            }
        }, Throws.Nothing);
    }

    private FlatDbManager CreateManager() => new(
        _resourcePool,
        _processExitSource,
        _trieNodeCache,
        _snapshotCompactor,
        _snapshotRepository,
        _persistenceManager,
        Substitute.For<IPersistedSnapshotLoader>(),
        new FlatDbConfig { CompactSize = 16, MaxInFlightCompactJob = 4, InlineCompaction = true, HistoryEnabled = true },
        _blocksConfig,
        LimboLogs.Instance,
        enableDetailedMetrics: false);

    // Historical re-execution runs through the read-only processing env, as in production;
    // HistoricalFlatDbManager rejects MainBlockProcessing at a trie-less historical state.
    private static FlatScopeProvider CreateScopeProvider(IFlatDbManager manager) => new(
        new MemDb(),
        manager,
        new FlatDbConfig { CompactSize = 16, HistoryEnabled = true },
        new NoopTrieWarmer(),
        ResourcePool.Usage.ReadOnlyProcessingEnv,
        LimboLogs.Instance,
        isReadOnly: false);
}
