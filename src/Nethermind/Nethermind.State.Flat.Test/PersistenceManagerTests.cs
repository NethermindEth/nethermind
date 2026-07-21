// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistenceManagerTests
{
    private PersistenceManager _persistenceManager = null!;
    private FlatDbConfig _config = null!;
    private TestFinalizedStateProvider _finalizedStateProvider = null!;
    private FlatTestContainer _tier = null!;
    private SnapshotRepository _snapshotRepository = null!;
    private IPersistence _persistence = null!;
    private IPersistedSnapshotCompactor _persistedSnapshotCompactor = null!;
    private ResourcePool _resourcePool = null!;
    private StateId Block0 = new(0, Keccak.EmptyTreeHash);

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig
        {
            CompactSize = 16,
            MinReorgDepth = 64,
            MaxInMemoryBaseSnapshotCount = 128 + 32,
            MaxReorgDepth = 256,
            LongFinalityMaxReorgDepth = 90000,
            EnableLongFinality = true
        };

        _resourcePool = new ResourcePool(_config);
        _finalizedStateProvider = new TestFinalizedStateProvider();
        // SnapshotRepository owns both tiers over a real temp-dir-backed persisted store, wired the
        // production way through FlatWorldStateModule; the container pairs it with its loader (load on
        // build, teardown on dispose).
        _tier = new FlatTestContainer();
        _snapshotRepository = _tier.Repository;
        _persistence = Substitute.For<IPersistence>();

        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        persistenceReader.CurrentState.Returns(Block0);
        _persistence.CreateReader().Returns(persistenceReader);

        _persistedSnapshotCompactor = Substitute.For<IPersistedSnapshotCompactor>();

        _persistenceManager = new PersistenceManager(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>());
    }

    [TearDown]
    public async Task TearDown()
    {
        _persistenceManager.Dispose();
        await _persistedSnapshotCompactor.DisposeAsync();
        _tier.Dispose();
    }

    private StateId CreateStateId(ulong blockNumber, byte rootByte = 0)
    {
        byte[] bytes = new byte[32];
        bytes[0] = rootByte;
        return new StateId(blockNumber, new ValueHash256(bytes));
    }

    private Snapshot CreateSnapshot(StateId from, StateId to, bool compacted = false)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);

        if (compacted)
        {
            _snapshotRepository.TryAdd(snapshot, SnapshotTier.InMemoryCompacted);
        }
        else
        {
            _snapshotRepository.TryAdd(snapshot, SnapshotTier.InMemoryBase);
        }

        // AddStateId is needed for GetStatesAtBlockNumber to work
        _snapshotRepository.AddStateId(to);

        return snapshot;
    }

    // Persist a base directly into the (real) persisted tier, bypassing the in-memory tier.
    private void PersistBase(StateId from, StateId to)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.MainBlockProcessing);
        snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);
        _tier.ConvertToPersistedBase(snapshot).Dispose();
    }

    private Snapshot CreateSnapshotWithSelfDestruct(StateId from, StateId to)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.SelfDestructedStorageAddresses[TestItem.AddressA] = false; // false = should be processed
        return snapshot;
    }

    [Test]
    public void DetermineSnapshotAction_InsufficientInMemoryDepth_ReturnsNull()
    {
        // Gate passes (60+16=76 > 64) but GetFinalizedStateRootAt(16) is not configured → seed = null.
        StateId persisted = Block0;
        StateId latest = CreateStateId(60);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Null);
    }

    [TestCase(true, TestName = "DetermineSnapshotAction_SufficientDepthAndFinalized_ReturnsCompactedSnapshot")]
    [TestCase(false, TestName = "DetermineSnapshotAction_SufficientDepthAndFinalized_BaseAtFinalizedBlock")]
    public void DetermineSnapshotAction_SufficientDepthAndFinalized(bool useCompacted)
    {
        // Persisted at Block0, latest at 100, finalized at the target block (= the single seed).
        // With CompactSize=16, finalized must be >= persisted + 16 for the normal-trigger seed to
        // engage; the non-compacted case uses a base at block 16 to satisfy that gate.
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);

        StateId target = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: useCompacted);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toConvert, Is.Null);
        Assert.That(toPersist!.From, Is.EqualTo(persisted));
        Assert.That(toPersist.To, Is.EqualTo(target));

        toPersist.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_FreshDb_PersistsGenesisBaseFirst()
    {
        // Nothing persisted yet (PreGenesis). The schedule anchors the next compaction boundary at
        // genesis (block 16) instead of the ulong.MaxValue "no further boundary" sentinel, so the
        // finalized trigger engages from a fresh DB. The genesis base (PreGenesis -> Block0) is the
        // first persistable chunk: a single PreGenesis -> 16 span would be 17 (> CompactSize), so the
        // walk persists the genesis base before the wider Block0 -> 16 chunk.
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(StateId.PreGenesis);

        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader().Returns(reader);

        using PersistenceManager pm = new(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>());

        // Depth 101 is past MinReorgDepth + CompactSize (80) but far below the force-persist backstop
        // (90000), so only the finalized branch can produce a snapshot here.
        StateId boundary = CreateStateId(16);
        StateId latest = CreateStateId(100);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(boundary.StateRoot.Bytes));

        using Snapshot genesis = CreateSnapshot(StateId.PreGenesis, Block0, compacted: false);
        using Snapshot boundaryChunk = CreateSnapshot(Block0, boundary, compacted: true);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = pm.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toPersist!.From, Is.EqualTo(StateId.PreGenesis));
        Assert.That(toPersist.To, Is.EqualTo(Block0));

        toPersist.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_UnfinalizedButBelowForceLimit_ReturnsNull()
    {
        // Depth (150) is below LongFinalityMaxReorgDepth (90000), so the backstop doesn't fire.
        // Finalized (10) < nextBoundary (16), so the normal-trigger gate also doesn't fire.
        // Neither Phase 1 path activates; Phase 2 is below the SnapshotCount threshold.
        StateId persisted = Block0;
        StateId latest = CreateStateId(150);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_LongFinalityDisabled_SkipsConversionPath()
    {
        // In-memory depth ~301, finality stalled at block 10. With EnableLongFinality off, the
        // conversion path must not fire and we must not invoke the converter.
        _config.EnableLongFinality = false;
        _persistenceManager = new PersistenceManager(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>());

        StateId persisted = Block0;
        StateId latest = CreateStateId(300);
        StateId target = CreateStateId(1);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: false);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        // The load-bearing check: the long-finality conversion path is short-circuited.
        // toPersist may still be populated by the normal finalized-snapshot-to-RocksDB
        // fall-through (its behaviour is unchanged), but no persisted-snapshot conversion
        // and no force-persisted-snapshot was returned.
        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toConvert, Is.Null, "Conversion path must be gated when EnableLongFinality is false");

        // Sanity: with the flag off no snapshot was converted into the persisted tier.
        toPersist?.Dispose();
        Assert.That(_snapshotRepository.PersistedSnapshotCount, Is.EqualTo(0));
    }

    [Test]
    public void DetermineSnapshotAction_BackstopExceeded_SeedsFromInMemoryTier()
    {
        // Backstop: snapshotsDepth (95000) > LongFinalityMaxReorgDepth (90000), finalized not in range.
        // Phase 1 must seed from the in-memory tier's latest registered state.
        StateId latest = CreateStateId(95000);
        // tierTip spans at most CompactSize from Block0 so the base it anchors is a persist candidate.
        StateId tierTip = CreateStateId(_config.CompactSize);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        // CreateSnapshot registers the snapshot's To, so GetLastSnapshotId returns tierTip and the backstop
        // seeds on it; emulate a one-hop graph by registering a base at tierTip with From = Block0.
        using Snapshot expected = CreateSnapshot(Block0, tierTip, compacted: false);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(toConvert, Is.Null);
        // The backstop seed lands on tierTip; the BFS finds the in-memory base whose From == Block0
        // (currentPersistedState) and returns it as toPersist.
        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toPersist!.From, Is.EqualTo(Block0));
        Assert.That(toPersist.To, Is.EqualTo(tierTip));

        toPersist.Dispose();
    }

    // With MinReorgDepth >= the configured backstop, the effective backstop is raised to
    // MinReorgDepth + CompactSize, so a depth just past the configured 90000 does NOT force-persist,
    // but one past MinReorgDepth + CompactSize does.
    [TestCase(90001, false, TestName = "DetermineSnapshotAction_BackstopRaised_BelowMinPlusCompactSize_NoForce")]
    [TestCase(90000 + 16 + 1, true, TestName = "DetermineSnapshotAction_BackstopRaised_AboveMinPlusCompactSize_Forces")]
    public void DetermineSnapshotAction_BackstopRaisedAboveMinReorgDepth(int latestBlock, bool expectForcedPersist)
    {
        // MinReorgDepth == configured backstop == 90000, CompactSize 16 → effective backstop 90016.
        FlatDbConfig config = new()
        {
            CompactSize = 16,
            MinReorgDepth = 90000,
            MaxReorgDepth = 90000,
            LongFinalityMaxReorgDepth = 90000,
            EnableLongFinality = true,
            MaxInMemoryBaseSnapshotCount = 160,
        };
        using PersistenceManager pm = new(
            config,
            ScheduleHelper.CreateWithOffset(config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>());

        // Finalized below the next boundary so only the backstop (not the finalized trigger) can fire;
        // a registered base at tierTip gives FindSnapshotToPersist a candidate.
        StateId tierTip = CreateStateId(config.CompactSize);
        using Snapshot expected = CreateSnapshot(Block0, tierTip, compacted: false);
        _finalizedStateProvider.SetFinalizedBlockNumber(5);

        (_, Snapshot? toPersist, _) = pm.DetermineSnapshotAction(CreateStateId((ulong)latestBlock));

        Assert.That(toPersist is not null, Is.EqualTo(expectForcedPersist));
        toPersist?.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_FinalizedGatePassesButSeedMissing_BackstopStillForcesPersist()
    {
        // Regression: with MinReorgDepth == the configured backstop (both 90000), the finalized
        // trigger's depth gate (depth + CompactSize > MinReorgDepth) is satisfied across the whole
        // operating range above the backstop. When the finalized branch is entered but yields no seed
        // (its synthetic boundary root resolves to null here), the backstop must STILL fire — it is an
        // independent fallback, not an `else if` shadowed by the always-satisfied finalized depth gate.
        // Before the fix this returned no persist candidate, so deep state never persisted.
        FlatDbConfig config = new()
        {
            CompactSize = 16,
            MinReorgDepth = 90000,
            MaxReorgDepth = 90000,
            LongFinalityMaxReorgDepth = 90000,
            EnableLongFinality = true,
            MaxInMemoryBaseSnapshotCount = 160,
        };
        using PersistenceManager pm = new(
            config,
            ScheduleHelper.CreateWithOffset(config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>());

        // Finalized at/above the next boundary so the finalized branch IS entered, but leave
        // GetFinalizedStateRootAt(16) unset so its seed resolves to null. Depth (90017) exceeds the
        // effective backstop (MinReorgDepth + CompactSize = 90016), so the backstop must persist.
        StateId tierTip = CreateStateId(config.CompactSize);
        using Snapshot expected = CreateSnapshot(Block0, tierTip, compacted: false);
        _finalizedStateProvider.SetFinalizedBlockNumber(90000);

        (_, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = pm.DetermineSnapshotAction(CreateStateId(90017));

        Assert.That(toPersist, Is.Not.Null, "Backstop must force a persist even when the finalized branch ran but found no seed");
        Assert.That(toPersist!.From, Is.EqualTo(Block0));
        Assert.That(toPersist.To, Is.EqualTo(tierTip));
        Assert.That(toConvert, Is.Null);
        toPersist.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_FinalizedBeyondHead_SeedsAtBoundary()
    {
        // Catch-up sync: CL reports a finalized block far beyond the local chain head.
        // GetFinalizedStateRootAt(finalizedBlockNumber) would return null, but the boundary
        // block (persisted + CompactSize) IS locally synced, so the canonical-root lookup
        // resolves there. Phase 1 must seed at the boundary and persist the boundary snapshot.
        StateId persisted = Block0;
        StateId latest = CreateStateId(200);
        StateId boundary = CreateStateId(_config.CompactSize);

        _finalizedStateProvider.SetFinalizedBlockNumber(25_128_361);
        // Deliberately leave GetFinalizedStateRootAt(25_128_361) unset → returns null;
        // only the boundary block has a known canonical state root.
        _finalizedStateProvider.SetFinalizedStateRootAt(_config.CompactSize, new Hash256(boundary.StateRoot.Bytes));

        using Snapshot expected = CreateSnapshot(persisted, boundary, compacted: false);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(toConvert, Is.Null);
        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toPersist!.From, Is.EqualTo(persisted));
        Assert.That(toPersist.To, Is.EqualTo(boundary));

        toPersist.Dispose();
    }

    [Test]
    public void TryFindSnapshotToConvert_PrefersBoundaryCompactedOverBase()
    {
        // Phase 2 must globally prefer a CompactSize-wide compacted (→ large repo via Branch A)
        // over any in-memory base (→ small repo via Branch B), regardless of block-number
        // ordering. Seed an in-memory base at state(1) and a CompactSize-wide (16-wide) compacted
        // at state(16) — both have From == Block0 on disk — and assert the compacted is picked.
        StateId persisted = Block0;
        StateId baseTo = CreateStateId(1);
        StateId compactedTo = CreateStateId(16);

        // Base at state(1) — sub-CompactSize; Branch B candidate.
        using Snapshot baseSnap = CreateSnapshot(persisted, baseTo, compacted: false);
        // 16-wide compacted from Block0 — boundary, should win under the two-pass form.
        using Snapshot compactedSnap = CreateSnapshot(persisted, compactedTo, compacted: true);

        PersistenceManager.ConversionCandidate? result = InvokeTryFindSnapshotToConvert(persisted);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Compacted, Is.Not.Null);
        Assert.That(result.Compacted!.From, Is.EqualTo(persisted));
        Assert.That(result.Compacted.To, Is.EqualTo(compactedTo));
        Assert.That(result.Base, Is.Null);

        result.Compacted.Dispose();
    }

    [Test]
    public void ConvertCompactedRange_BoundaryCompacted_RemovesOnlyConvertedStates_PreservingOutsider()
    {
        // Branch A converts the in-memory bases spanning the boundary compacted's range, then must
        // remove ONLY those gathered states from the in-memory tier. A state outside the gathered
        // range (here one below `start`, standing in for a snapshot added concurrently mid-convert)
        // must survive — the old bulk RemoveStatesUntil(end) would have wrongly swept it.
        StateId compactedFrom = CreateStateId(2);
        StateId compactedTo = CreateStateId(2 + _config.CompactSize); // span == CompactSize → Branch A
        StateId baseA = CreateStateId(5);
        StateId baseB = CreateStateId(10);
        StateId outsider = CreateStateId(1); // below start (= compactedFrom.BlockNumber + 1)

        // ConvertCompactedRange persists the gathered snapshot into the real persisted tier.
        // The converted/boundary snapshots are disposed by it (via RemoveAndRelease + the
        // pre-leased candidate), so they are NOT wrapped in `using`. Only the survivor is.
        CreateSnapshot(compactedFrom, compactedTo, compacted: true);
        CreateSnapshot(compactedFrom, baseA, compacted: false);
        CreateSnapshot(baseA, baseB, compacted: false);
        using Snapshot outsiderSnap = CreateSnapshot(Block0, outsider, compacted: false);

        Assert.That(_snapshotRepository.HasState(outsider), Is.True);

        _snapshotRepository.TryLeaseInMemoryState(compactedTo, SnapshotTier.InMemoryCompacted, out Snapshot? compactedForConvert);
        InvokeConvertCompactedRange(compactedForConvert!);

        Assert.Multiple(() =>
        {
            Assert.That(_snapshotRepository.HasState(outsider), Is.True, "state below `start` must survive");
            // Gathered states are converted into the persisted tier (so HasState still sees them) but
            // must be dropped from the in-memory tier — check in-memory presence via TryLeaseInMemoryState.
            Assert.That(_snapshotRepository.TryLeaseInMemoryState(baseA, SnapshotTier.InMemoryBase, out _), Is.False, "baseA removed from the in-memory tier");
            Assert.That(_snapshotRepository.TryLeaseInMemoryState(baseB, SnapshotTier.InMemoryBase, out _), Is.False, "baseB removed from the in-memory tier");
            Assert.That(_snapshotRepository.TryLeaseInMemoryState(compactedTo, SnapshotTier.InMemoryCompacted, out _), Is.False, "boundary compacted removed");
        });
    }

    [Test]
    public async Task AddToPersistence_InMemoryPersist_PrunesPersistedTier()
    {
        // Persisting an in-memory snapshot must trigger RemoveStatesUntil on both tier repos so
        // superseded tier entries get cleared — the toPersist branch must prune, not only the
        // persistedToPersist branch.
        StateId from = Block0;
        StateId to = CreateStateId(16);
        StateId latest = CreateStateId(100);

        // AddToPersistence persists then prunes this in-memory snapshot, so the repo owns its disposal.
        _ = CreateSnapshot(from, to, compacted: true);

        // A persisted entry below the new persisted block must be pruned by the persist.
        StateId stale = CreateStateId(8);
        PersistBase(Block0, stale);
        Assert.That(_snapshotRepository.HasBasePersistedSnapshot(stale), Is.True);

        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        await _persistenceManager.AddToPersistence(latest);

        // Persisting the in-memory snapshot at `to` must prune the persisted tier below `to`.
        Assert.That(_snapshotRepository.HasBasePersistedSnapshot(stale), Is.False);
    }

    [Test]
    public async Task AddToPersistence_TierSourcePersist_PrunesPersistedTier()
    {
        // Sibling of AddToPersistence_InMemoryPersist_PrunesPersistedTier for the persistedToPersist
        // branch. Tier-source persists must also drive RemoveStatesUntil so superseded entries are cleared.
        StateId target = CreateStateId(16);
        StateId latest = CreateStateId(100);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        // No in-memory snapshot — DetermineSnapshotAction takes the tier-fallback path and persists
        // the base in the persisted tier whose From == the current persisted state (Block0).
        PersistBase(Block0, target);
        // A persisted entry below `target` must be pruned by the persist.
        StateId stale = CreateStateId(8);
        PersistBase(Block0, stale);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        await _persistenceManager.AddToPersistence(latest);

        Assert.That(_snapshotRepository.HasBasePersistedSnapshot(stale), Is.False);
    }

    [Test]
    public void DetermineSnapshotAction_UnfinalizedBelowBackstop_ReturnsNull()
    {
        // Unfinalized (finalized at 10, persisted at 0 — not in range for the CompactSize=16
        // gate) AND in-memory depth (300) below LongFinalityMaxReorgDepth (90000): no force-persist,
        // no Phase 1 candidate. Phase 2 entry guard (SnapshotCount > 160) also not satisfied with
        // a single created snapshot. Action: do nothing.
        StateId persisted = Block0;
        StateId latest = CreateStateId(300);
        StateId target = CreateStateId(1);

        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: false);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_UnfinalizedForkAtBoundary_PersistsHeadReachableFork()
    {
        // Two unfinalized forks at the boundary block 16, both starting from Block0. The committed head's
        // chain runs through target2, not the arbitrary target1. The backstop force-persist must follow the
        // committed head's chain (target2) — persisting target1 would orphan the head.
        StateId persisted = Block0;
        StateId target1 = CreateStateId(16, rootByte: 1); // off-chain fork
        StateId target2 = CreateStateId(16, rootByte: 2); // on the committed head's chain
        StateId head = CreateStateId(95000); // depth > LongFinalityMaxReorgDepth (90000) → backstop fires

        _finalizedStateProvider.SetFinalizedBlockNumber(10); // unfinalized at the boundary

        using Snapshot fork1 = CreateSnapshot(persisted, target1, compacted: true);
        using Snapshot fork2 = CreateSnapshot(persisted, target2, compacted: true);
        using Snapshot toHead = CreateSnapshot(target2, head, compacted: true); // head reachable only via target2
        _snapshotRepository.SetLastCommittedStateId(head);

        (_, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(head);

        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toPersist!.From, Is.EqualTo(persisted));
        Assert.That(toPersist.To, Is.EqualTo(target2));

        toPersist.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_LongerNonCanonicalFork_PersistsCommittedHeadChain()
    {
        // The longest in-memory chain runs through target1 (longHead is the max, so GetLastSnapshotId would
        // pick it), but the committed head is the shorter chain through target2. The backstop must follow the
        // committed head (target2), not the longer fork (target1) that the GetLastSnapshotId fallback would pick.
        StateId persisted = Block0;
        StateId target1 = CreateStateId(16, rootByte: 1); // boundary state on the longer, non-canonical fork
        StateId target2 = CreateStateId(16, rootByte: 2); // boundary state on the committed head's chain
        StateId longHead = CreateStateId(95001, rootByte: 1); // longest chain, but not committed
        StateId committedHead = CreateStateId(95000, rootByte: 2);

        _finalizedStateProvider.SetFinalizedBlockNumber(0); // unfinalized at the boundary

        // longHead (block 95001) is the max, so the GetLastSnapshotId fallback would pick the longer fork —
        // only honouring the committed head selects target2.
        using Snapshot fork2 = CreateSnapshot(persisted, target2, compacted: true);
        using Snapshot toCommittedHead = CreateSnapshot(target2, committedHead, compacted: true);
        using Snapshot fork1 = CreateSnapshot(persisted, target1, compacted: true);
        using Snapshot toLongHead = CreateSnapshot(target1, longHead, compacted: true);
        _snapshotRepository.SetLastCommittedStateId(committedHead);

        // latestSnapshot at the longest chain makes the in-memory depth exceed LongFinalityMaxReorgDepth, triggering the
        // force-persist (backstop) branch.
        (_, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(longHead);

        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toPersist!.From, Is.EqualTo(persisted));
        Assert.That(toPersist.To, Is.EqualTo(target2));

        toPersist.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_NoSnapshotAvailable_ReturnsNull()
    {
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(CreateStateId(16).StateRoot.Bytes));

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_FinalizedNoInMemory_FallsBackToPersistedSnapshot()
    {
        // Setup: persisted at Block0, latest at 100, finalized at 16 — the BFS seeds with the
        // finalized state, which corresponds exactly to the persisted snapshot we mock below.
        StateId latest = CreateStateId(100);
        StateId target = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        // Don't create any in-memory snapshots — persist a base into the tier so the fallback finds it.
        PersistBase(Block0, target);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Not.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Null);

        persistedToPersist!.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_SnapshotWithWrongFromState_ReturnsNull()
    {
        // Setup: snapshot exists but doesn't start from current persisted state
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);
        StateId wrongFrom = CreateStateId(5);
        StateId target = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        using Snapshot wrongSnapshot = CreateSnapshot(wrongFrom, target, compacted: true);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_MultipleStatesAtBlock_SelectsCorrectOne()
    {
        // Setup: multiple state roots at same block number (reorg scenario). Set finalized at the
        // candidate block so the single-seed BFS lands directly on the finalized state root.
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);
        StateId target1 = CreateStateId(16, rootByte: 1);
        StateId target2 = CreateStateId(16, rootByte: 2);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target2.StateRoot.Bytes));

        using Snapshot snapshot1 = CreateSnapshot(persisted, target1, compacted: true);
        using Snapshot snapshot2 = CreateSnapshot(persisted, target2, compacted: true);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toPersist!.To.StateRoot.Bytes.ToArray(), Is.EqualTo(target2.StateRoot.Bytes.ToArray()));

        toPersist.Dispose();
    }

    public void DetermineSnapshotAction_LatestSnapshotBelowPersistedBlock_ReturnsNullWithoutUnderflow()
    {
        // A deep reorg below a force-persisted unfinalized block can leave the latest snapshot behind the
        // last persisted block. The in-memory depth must saturate to 0 (not underflow to ~2^64) so the
        // backstop does not fire and force-persist a stale head ancestor.
        StateId persisted = CreateStateId(100);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(persisted);

        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader().Returns(reader);

        using PersistenceManager pm = new(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>());

        // Latest snapshot (50) is below the persisted block (100); finalized far behind so the buggy
        // underflow path would take the backstop branch. Stage a head-ancestor snapshot it would return.
        StateId latest = CreateStateId(50);
        StateId headAncestor = CreateStateId(101);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        using Snapshot staged = CreateSnapshot(persisted, headAncestor, compacted: false);
        _snapshotRepository.SetLastCommittedStateId(headAncestor);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = pm.DetermineSnapshotAction(latest);
        using Snapshot? toDispose = toPersist; // dispose if the buggy underflow path returned a snapshot

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_ExactlyAtMinimumBoundary_ReturnsNull()
    {
        // Gate passes (79+16=95 > 64), but GetFinalizedStateRootAt(16) is not configured →
        // returns null → seed = null. No backstop (79 << LongFinalityMaxReorgDepth). Result: null.
        StateId persisted = Block0;
        StateId latest = CreateStateId(79);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_OneAboveMinimumBoundary_ReturnsSnapshot()
    {
        // Setup: persisted at Block0, latest at 80, finalized at the candidate block (16) so the
        // single-seed BFS lands directly on it. Depth (80) + CompactSize (16) = 96 > MinReorgDepth
        // (64) — passes the normal-trigger gate.
        StateId persisted = Block0;
        StateId latest = CreateStateId(80);
        StateId target = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: true);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Not.Null);

        toPersist!.Dispose();
    }

    [Test]
    public void PersistSnapshot_WithAccountsStorageAndTrieNodes_WritesToBatch()
    {
        // Arrange
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        // Add accounts
        snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);
        snapshot.Content.Accounts[TestItem.AddressB] = new Account(2, 200);

        // Add storage
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)1)] = SlotValue.FromSpanWithoutLeadingZero([42]);
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)2)] = SlotValue.FromSpanWithoutLeadingZero([99]);

        // Add trie nodes
        TreePath path = TreePath.Empty;
        TrieNode node = new(NodeType.Leaf, Keccak.Zero);
        snapshot.Content.StateNodes[path] = node;

        FakeWriteBatch writeBatch = new();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        // Act
        _persistenceManager.PersistSnapshot(snapshot);

        // Assert
        Assert.That(writeBatch.SetAccountCalls, Has.Some.Matches<(Address Addr, Account? Account)>(c => c.Addr == TestItem.AddressA));
        Assert.That(writeBatch.SetAccountCalls, Has.Some.Matches<(Address Addr, Account? Account)>(c => c.Addr == TestItem.AddressB));
        Assert.That(writeBatch.SetStorageCalls, Has.Some.Matches<(Address Addr, UInt256 Slot, SlotValue? Value)>(c => c.Addr == TestItem.AddressA && c.Slot == (UInt256)1));
        Assert.That(writeBatch.SetStorageCalls, Has.Some.Matches<(Address Addr, UInt256 Slot, SlotValue? Value)>(c => c.Addr == TestItem.AddressA && c.Slot == (UInt256)2));
        Assert.That(writeBatch.SetStateTrieNodeCalls, Is.Not.Empty);
        Assert.That(node.IsPersisted, Is.True);
    }

    [Test]
    public void PersistSnapshot_WithSelfDestructedAddresses_CallsSelfDestruct()
    {
        // Arrange
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = CreateSnapshotWithSelfDestruct(from, to);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        // Act
        _persistenceManager.PersistSnapshot(snapshot);

        // Assert
        writeBatch.Received().SelfDestruct(TestItem.AddressA);
    }

    [Test]
    public void PersistSnapshot_EmptySnapshot_CreatesWriteBatch()
    {
        // Arrange
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        // Act
        _persistenceManager.PersistSnapshot(snapshot);

        // Assert
        _persistence.Received(1).CreateWriteBatch(from, to);
    }

    [Test]
    public async Task AddToPersistence_WithAvailableSnapshot_PersistsAndUpdatesState()
    {
        // Finalized at the candidate block so the single-seed BFS lands directly on it.
        StateId from = Block0;
        StateId to = CreateStateId(16);
        StateId latest = CreateStateId(100);

        // AddToPersistence persists then prunes this in-memory snapshot, so the repo owns its disposal.
        _ = CreateSnapshot(from, to, compacted: true);

        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        await _persistenceManager.AddToPersistence(latest);

        _persistence.Received().CreateWriteBatch(from, to);
        Assert.That(_persistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(to));
    }

    [Test]
    public async Task AddToPersistence_WithCaptureHook_CapturesHistoryUpToPersistedBlock()
    {
        RecordingCaptureHook hook = new();
        using PersistenceManager manager = new(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>(),
            hook);

        StateId from = Block0;
        StateId to = CreateStateId(16);
        StateId latest = CreateStateId(100);
        _ = CreateSnapshot(from, to, compacted: true);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(Substitute.For<IPersistence.IWriteBatch>());

        await manager.AddToPersistence(latest);

        Assert.That(hook.CapturedUpTo, Is.EqualTo(to));
    }

    // FlushToPersistence prunes both tiers as it drains, so a flush without capture would leave the flushed
    // range permanently absent from history on every shutdown.
    [Test]
    public void FlushToPersistence_WithCaptureHook_CapturesTheFlushedRange()
    {
        RecordingCaptureHook hook = new();
        using PersistenceManager manager = new(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>(),
            hook);

        StateId to = CreateStateId(16);
        _ = CreateSnapshot(Block0, to, compacted: true);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(Substitute.For<IPersistence.IWriteBatch>());

        StateId flushed = manager.FlushToPersistence();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flushed, Is.EqualTo(to));
            Assert.That(hook.CapturedUpTo, Is.EqualTo(to), "the flushed range must be captured before it is pruned");
        }
    }

    [Test]
    public async Task AddToPersistence_WhenCaptureHookThrows_StillPersists()
    {
        using PersistenceManager manager = new(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>(),
            new ThrowingCaptureHook());

        StateId from = Block0;
        StateId to = CreateStateId(16);
        StateId latest = CreateStateId(100);
        _ = CreateSnapshot(from, to, compacted: true);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(Substitute.For<IPersistence.IWriteBatch>());

        await manager.AddToPersistence(latest);

        Assert.That(manager.GetCurrentPersistedStateId(), Is.EqualTo(to));
    }

    [Test]
    public async Task AddToPersistence_CapturesHistoryBeforeAdvancingTheBarrier()
    {
        PersistenceManager manager = null!;
        BarrierObservingCaptureHook hook = new(() => manager.GetCurrentPersistedStateId());
        manager = new(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _tier.Loader,
            Substitute.For<IProcessExitSource>(),
            hook);
        using PersistenceManager managerScope = manager;

        StateId from = Block0;
        StateId to = CreateStateId(16);
        StateId latest = CreateStateId(100);
        _ = CreateSnapshot(from, to, compacted: true);
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(Substitute.For<IPersistence.IWriteBatch>());

        StateId barrierBefore = manager.GetCurrentPersistedStateId();
        await manager.AddToPersistence(latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hook.BarrierAtCapture, Is.EqualTo(barrierBefore), "history must be captured before the persisted-state barrier is published");
            Assert.That(manager.GetCurrentPersistedStateId(), Is.EqualTo(to));
        }
    }

    [Test]
    public void FlushToPersistence_NoSnapshots_ReturnsCurrentPersistedState()
    {
        // Arrange - no snapshots added
        StateId persisted = Block0;

        // Act
        StateId result = _persistenceManager.FlushToPersistence();

        // Assert
        Assert.That(result, Is.EqualTo(persisted));
    }

    [Test]
    public void FlushToPersistence_WithFinalizedSnapshots_PersistsFinalizedFirst()
    {
        StateId state16 = CreateStateId(16);
        StateId state32 = CreateStateId(32);

        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(state16.StateRoot.Bytes));
        _finalizedStateProvider.SetFinalizedStateRootAt(32, new Hash256(state32.StateRoot.Bytes));

        // Repo-owned; FlushToPersistence prunes (disposes) them once persisted, so don't double-own.
        CreateSnapshot(Block0, state16, compacted: true);
        CreateSnapshot(state16, state32, compacted: true);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(state32));
        _persistence.Received().CreateWriteBatch(Block0, state16);
        _persistence.Received().CreateWriteBatch(state16, state32);
    }

    [Test]
    public void FlushToPersistence_WithUnfinalizedSnapshots_FallsBackToFirstAvailable()
    {
        StateId state16 = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(0);

        // Repo-owned; FlushToPersistence prunes (disposes) it once persisted, so don't double-own.
        CreateSnapshot(Block0, state16, compacted: true);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(state16));
        _persistence.Received().CreateWriteBatch(Block0, state16);
    }

    [Test]
    public void FlushToPersistence_UnfinalizedForkAtBoundary_PersistsHeadReachableFork()
    {
        // Two unfinalized forks at the boundary block 16; the head's chain runs through target2. The flush
        // must persist target2 (head-reachable), not the arbitrary first fork target1.
        StateId target1 = CreateStateId(16, rootByte: 1); // arbitrary "first" (lowest root)
        StateId target2 = CreateStateId(16, rootByte: 2); // on the head's chain
        StateId head = CreateStateId(32);

        _finalizedStateProvider.SetFinalizedBlockNumber(0); // nothing finalized

        // Repo-owned; FlushToPersistence persists/prunes (disposes) them, so don't double-own.
        CreateSnapshot(Block0, target1, compacted: true);
        CreateSnapshot(Block0, target2, compacted: true);
        CreateSnapshot(target2, head, compacted: true); // head reachable only via target2
        _snapshotRepository.SetLastCommittedStateId(head);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(head));
        _persistence.Received().CreateWriteBatch(Block0, target2);
        _persistence.DidNotReceive().CreateWriteBatch(Block0, target1);
    }

    [Test]
    public void FlushToPersistence_LongerNonCanonicalFork_PersistsCommittedHeadChain()
    {
        // The longest in-memory chain runs through target1 to block 300, but the committed head is the
        // shorter chain through target2 (at block 32). The flush must follow the committed head (target2),
        // stopping at its block, not chase the longer non-canonical fork through target1.
        StateId target1 = CreateStateId(16, rootByte: 1); // boundary state on the longer, non-canonical fork
        StateId target2 = CreateStateId(16, rootByte: 2); // boundary state on the committed head's chain
        StateId longHead = CreateStateId(300); // longest chain (the max), but not committed
        StateId committedHead = CreateStateId(32, rootByte: 2);

        _finalizedStateProvider.SetFinalizedBlockNumber(0); // nothing finalized

        // Repo-owned; FlushToPersistence persists/prunes (disposes) them, so don't double-own.
        CreateSnapshot(Block0, target1, compacted: true);
        CreateSnapshot(Block0, target2, compacted: true);
        CreateSnapshot(target1, longHead, compacted: true);
        CreateSnapshot(target2, committedHead, compacted: true);
        _snapshotRepository.SetLastCommittedStateId(committedHead);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(committedHead));
        _persistence.Received().CreateWriteBatch(Block0, target2);
        _persistence.DidNotReceive().CreateWriteBatch(Block0, target1);
    }

    [Test]
    public void FlushToPersistence_PrefersFinalizedOverUnfinalized()
    {
        // Two snapshots at the same block, one finalized. Set finalized block to the
        // candidate block so the BFS seed lands directly on the finalized state.
        StateId finalizedState = CreateStateId(16, rootByte: 1);
        StateId unfinalizedState = CreateStateId(16, rootByte: 2);

        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(finalizedState.StateRoot.Bytes));

        // Repo-owned; FlushToPersistence prunes (disposes) them once persisted, so don't double-own.
        CreateSnapshot(Block0, finalizedState, compacted: true);
        CreateSnapshot(Block0, unfinalizedState, compacted: true);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result.StateRoot.Bytes.ToArray(), Is.EqualTo(finalizedState.StateRoot.Bytes.ToArray()));
    }

    [Test]
    public void FlushToPersistence_PersistsMultipleSnapshots_InOrder()
    {
        StateId state1 = CreateStateId(1);
        StateId state2 = CreateStateId(2);
        StateId state3 = CreateStateId(3);

        _finalizedStateProvider.SetFinalizedBlockNumber(0);

        // Repo-owned; FlushToPersistence prunes (disposes) them once persisted, so don't double-own.
        CreateSnapshot(Block0, state1, compacted: false);
        CreateSnapshot(state1, state2, compacted: false);
        CreateSnapshot(state2, state3, compacted: false);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(state3));
        Received.InOrder(() =>
        {
            _persistence.CreateWriteBatch(Block0, state1);
            _persistence.CreateWriteBatch(state1, state2);
            _persistence.CreateWriteBatch(state2, state3);
        });
    }

    [Test]
    public void FlushToPersistence_PersistedOnlyTier_WalksAndPrunes()
    {
        // No in-memory snapshot above the persisted point and nothing finalized: the flush must
        // still reach the persisted-tier backlog via the tier-aware latest tip (GetLastSnapshotId
        // folds in the persisted maxes) and prune entries the persist supersedes. Regression for
        // FlushToPersistence early-returning on a persisted-only tier and never pruning it.
        StateId target = CreateStateId(16);
        StateId stale = CreateStateId(8);

        PersistBase(Block0, stale);
        PersistBase(Block0, target);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(target));
        _persistence.Received().CreateWriteBatch(Block0, target);
        Assert.That(_snapshotRepository.HasBasePersistedSnapshot(stale), Is.False);
    }

    private PersistenceManager.ConversionCandidate? InvokeTryFindSnapshotToConvert(StateId currentPersistedState)
    {
        // TryFindSnapshotToConvert is private; reach it via reflection so we can unit-test the
        // priority logic without driving the full DetermineSnapshotAction → AddToPersistence loop.
        System.Reflection.MethodInfo method = typeof(PersistenceManager).GetMethod(
            "TryFindSnapshotToConvert",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (PersistenceManager.ConversionCandidate?)method.Invoke(_persistenceManager, [currentPersistedState]);
    }

    private void InvokeConvertCompactedRange(Snapshot compacted)
    {
        // ConvertCompactedRange is private; reach it via reflection to unit-test the in-memory
        // removal logic directly without driving the full DetermineSnapshotAction → AddToPersistence loop.
        System.Reflection.MethodInfo method = typeof(PersistenceManager).GetMethod(
            "ConvertCompactedRange",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(_persistenceManager, [compacted]);
    }

    private class TestFinalizedStateProvider : IFinalizedStateProvider
    {
        private ulong _finalizedBlockNumber;
        private readonly Dictionary<ulong, Hash256> _finalizedStateRoots = [];

        public ulong FinalizedBlockNumber => _finalizedBlockNumber;

        public void SetFinalizedBlockNumber(ulong blockNumber) => _finalizedBlockNumber = blockNumber;

        public void SetFinalizedStateRootAt(ulong blockNumber, Hash256 stateRoot) => _finalizedStateRoots[blockNumber] = stateRoot;

        public Hash256? GetFinalizedStateRootAt(ulong blockNumber) =>
            _finalizedStateRoots.TryGetValue(blockNumber, out Hash256? root) ? root : null;
    }

    private sealed class RecordingCaptureHook : IFlatPersistenceCaptureHook
    {
        public StateId? CapturedUpTo { get; private set; }

        public void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository) =>
            CapturedUpTo = persistedHead;
    }

    private sealed class ThrowingCaptureHook : IFlatPersistenceCaptureHook
    {
        public void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository) =>
            throw new System.InvalidOperationException("simulated history capture failure");
    }

    private sealed class BarrierObservingCaptureHook(System.Func<StateId> readBarrier) : IFlatPersistenceCaptureHook
    {
        public StateId BarrierAtCapture { get; private set; }

        public void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository) =>
            BarrierAtCapture = readBarrier();
    }
}
