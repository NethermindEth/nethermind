// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
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
    private SnapshotRepository _snapshotRepository = null!;
    private IPersistence _persistence = null!;
    private IPersistedSnapshotCompactor _persistedSnapshotCompactor = null!;
    private IPersistedSnapshotConverter _converter = null!;
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
            LongFinalityReorgDepth = 90000,
            EnableLongFinality = true
        };

        _resourcePool = new ResourcePool(_config);
        _finalizedStateProvider = new TestFinalizedStateProvider();
        // SnapshotRepository now owns both tiers over a real temp-dir-backed persisted store.
        _snapshotRepository = SnapshotRepositoryTestFactory.Create();
        _converter = new PersistedSnapshotConverter(
            _snapshotRepository.ArenaManager, _snapshotRepository.BlobArenaManager, _config, _snapshotRepository);
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
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _converter);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _persistenceManager.DisposeAsync();
        await _persistedSnapshotCompactor.DisposeAsync();
        _snapshotRepository.Dispose();
    }

    private StateId CreateStateId(long blockNumber, byte rootByte = 0)
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
        _snapshotRepository.ConvertToPersistedBase(snapshot).Dispose();
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
        // Setup: persisted at Block0 (0), latest at 60, after persist would be < 64 minimum
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
    public void DetermineSnapshotAction_UnfinalizedButBelowForceLimit_ReturnsNull()
    {
        // Setup: persisted at Block0, latest at 150, finalized at 10 (way behind)
        // After persist would be at 16, which is > finalized
        // But in-memory depth is 150 (< 256 forced boundary)
        StateId persisted = Block0;
        StateId latest = CreateStateId(150);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, PersistenceManager.ConversionCandidate? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Null);
    }

    [Test]
    public async Task DetermineSnapshotAction_LongFinalityDisabled_SkipsConversionPath()
    {
        // In-memory depth ~301, finality stalled at block 10. With EnableLongFinality off, the
        // conversion path must not fire and we must not invoke the converter.
        await _persistenceManager.DisposeAsync();
        _config.EnableLongFinality = false;
        _persistenceManager = new PersistenceManager(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _converter);

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
        // Backstop: snapshotsDepth (95000) > LongFinalityReorgDepth (90000), finalized not in range.
        // Phase 1 must seed from the in-memory tier's latest registered state.
        StateId latest = CreateStateId(95000);
        StateId tierTip = CreateStateId(80000);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        // Seed the in-memory base chain that the BFS will walk from tierTip back to Block0.
        // CreateSnapshot registers the snapshot's To as the in-memory tier's LastRegisteredState,
        // so the backstop seeds on tierTip; emulate a one-hop graph by registering a base at the
        // tier-tip block with From = Block0.
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

        // Base at state(1) — sub-CompactSize, would have triggered Branch B in the old code.
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
    public void DoConvert_BoundaryCompacted_RemovesOnlyConvertedStates_PreservingOutsider()
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

        // DoConvert persists the gathered snapshot into the real persisted tier.
        // The converted/boundary snapshots are disposed by DoConvert (via RemoveAndRelease + the
        // pre-leased candidate), so they are NOT wrapped in `using`. Only the survivor is.
        CreateSnapshot(compactedFrom, compactedTo, compacted: true);
        CreateSnapshot(compactedFrom, baseA, compacted: false);
        CreateSnapshot(baseA, baseB, compacted: false);
        using Snapshot outsiderSnap = CreateSnapshot(Block0, outsider, compacted: false);

        Assert.That(_snapshotRepository.HasState(outsider), Is.True);

        _snapshotRepository.TryLeaseInMemoryState(compactedTo, SnapshotTier.InMemoryCompacted, out Snapshot? compactedForConvert);
        InvokeDoConvert(new PersistenceManager.ConversionCandidate(compactedForConvert!, Base: null));

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
    public void AddToPersistence_InMemoryPersist_PrunesPersistedTier()
    {
        // Persisting an in-memory snapshot must trigger RemoveStatesUntil on both tier repos so
        // superseded tier entries get cleared — the toPersist branch must prune, not only the
        // persistedToPersist branch.
        StateId from = Block0;
        StateId to = CreateStateId(16);
        StateId latest = CreateStateId(100);

        using Snapshot snapshot = CreateSnapshot(from, to, compacted: true);

        // A persisted entry below the new persisted block must be pruned by the persist.
        StateId stale = CreateStateId(8);
        PersistBase(Block0, stale);
        Assert.That(_snapshotRepository.HasBaseSnapshot(stale), Is.True);

        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        _persistenceManager.AddToPersistence(latest);

        // Persisting the in-memory snapshot at `to` must prune the persisted tier below `to`.
        Assert.That(_snapshotRepository.HasBaseSnapshot(stale), Is.False);
    }

    [Test]
    public void AddToPersistence_TierSourcePersist_PrunesPersistedTier()
    {
        // Sibling of AddToPersistence_InMemoryPersist_PrunesPersistedTier for the
        // persistedToPersist branch at PersistenceManager line 426-432. Tier-source
        // persists must also drive RemoveStatesUntil so the in-memory tier doesn't keep growing
        // with entries that RocksDB now supersedes.
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

        _persistenceManager.AddToPersistence(latest);

        Assert.That(_snapshotRepository.HasBaseSnapshot(stale), Is.False);
    }

    [Test]
    public void DetermineSnapshotAction_UnfinalizedBelowBackstop_ReturnsNull()
    {
        // Unfinalized (finalized at 10, persisted at 0 — not in range for the CompactSize=16
        // gate) AND in-memory depth (300) below LongFinalityReorgDepth (90000): no force-persist,
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
    public void DetermineSnapshotAction_NoSnapshotAvailable_ReturnsNull()
    {
        // Setup: sufficient depth but no snapshots in repository
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
        StateId target2 = CreateStateId(16, rootByte: 2); // Different root
        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target2.StateRoot.Bytes)); // target2 is finalized

        using Snapshot snapshot1 = CreateSnapshot(persisted, target1, compacted: true);
        using Snapshot snapshot2 = CreateSnapshot(persisted, target2, compacted: true);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toPersist!.To.StateRoot.Bytes.ToArray(), Is.EqualTo(target2.StateRoot.Bytes.ToArray()));

        toPersist.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_ExactlyAtMinimumBoundary_ReturnsNull()
    {
        // Setup: persisted at Block0 (0), latest at 79
        // After persist would be at 15, leaving depth of 64 (exactly at minimum boundary)
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
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);
        snapshot.Content.Accounts[TestItem.AddressB] = new Account(2, 200);

        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)1)] = SlotValue.FromSpanWithoutLeadingZero([42]);
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)2)] = SlotValue.FromSpanWithoutLeadingZero([99]);

        TreePath path = TreePath.Empty;
        TrieNode node = new(NodeType.Leaf, Keccak.Zero);
        snapshot.Content.StateNodes[path] = node;

        FakeWriteBatch writeBatch = new();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        _persistenceManager.PersistSnapshot(snapshot);

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
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = CreateSnapshotWithSelfDestruct(from, to);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        _persistenceManager.PersistSnapshot(snapshot);

        writeBatch.Received().SelfDestruct(TestItem.AddressA);
    }

    [Test]
    public void PersistSnapshot_EmptySnapshot_CreatesWriteBatch()
    {
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        _persistenceManager.PersistSnapshot(snapshot);

        _persistence.Received(1).CreateWriteBatch(from, to);
    }

    [Test]
    public void AddToPersistence_WithAvailableSnapshot_PersistsAndUpdatesState()
    {
        // Finalized at the candidate block so the single-seed BFS lands directly on it.
        StateId from = Block0;
        StateId to = CreateStateId(16);
        StateId latest = CreateStateId(100);

        using Snapshot snapshot = CreateSnapshot(from, to, compacted: true);

        _finalizedStateProvider.SetFinalizedBlockNumber(16);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        _persistenceManager.AddToPersistence(latest);

        _persistence.Received().CreateWriteBatch(from, to);
        Assert.That(_persistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(to));
    }

    [Test]
    public void FlushToPersistence_NoSnapshots_ReturnsCurrentPersistedState()
    {
        StateId persisted = Block0;

        StateId result = _persistenceManager.FlushToPersistence();

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

        using Snapshot snapshot1 = CreateSnapshot(Block0, state16, compacted: true);
        using Snapshot snapshot2 = CreateSnapshot(state16, state32, compacted: true);

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
        _finalizedStateProvider.SetFinalizedBlockNumber(0); // Nothing finalized

        using Snapshot snapshot = CreateSnapshot(Block0, state16, compacted: true);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(state16));
        _persistence.Received().CreateWriteBatch(Block0, state16);
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

        using Snapshot finalizedSnapshot = CreateSnapshot(Block0, finalizedState, compacted: true);
        using Snapshot unfinalizedSnapshot = CreateSnapshot(Block0, unfinalizedState, compacted: true);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        // Should persist the finalized state.
        Assert.That(result.StateRoot.Bytes.ToArray(), Is.EqualTo(finalizedState.StateRoot.Bytes.ToArray()));
    }

    [Test]
    public void FlushToPersistence_PersistsMultipleSnapshots_InOrder()
    {
        StateId state1 = CreateStateId(1);
        StateId state2 = CreateStateId(2);
        StateId state3 = CreateStateId(3);

        // No finalization - will use first available
        _finalizedStateProvider.SetFinalizedBlockNumber(0);

        using Snapshot snapshot1 = CreateSnapshot(Block0, state1, compacted: false);
        using Snapshot snapshot2 = CreateSnapshot(state1, state2, compacted: false);
        using Snapshot snapshot3 = CreateSnapshot(state2, state3, compacted: false);

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

    private PersistenceManager.ConversionCandidate? InvokeTryFindSnapshotToConvert(StateId currentPersistedState)
    {
        // TryFindSnapshotToConvert is private; reach it via reflection so we can unit-test the
        // priority logic without driving the full DetermineSnapshotAction → AddToPersistence loop.
        System.Reflection.MethodInfo method = typeof(PersistenceManager).GetMethod(
            "TryFindSnapshotToConvert",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (PersistenceManager.ConversionCandidate?)method.Invoke(_persistenceManager, [currentPersistedState]);
    }

    private void InvokeDoConvert(PersistenceManager.ConversionCandidate candidate)
    {
        // DoConvert is private; reach it via reflection to unit-test the in-memory removal logic
        // directly without driving the full DetermineSnapshotAction → AddToPersistence loop.
        System.Reflection.MethodInfo method = typeof(PersistenceManager).GetMethod(
            "DoConvert",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(_persistenceManager, [candidate]);
    }

    private class TestFinalizedStateProvider : IFinalizedStateProvider
    {
        private long _finalizedBlockNumber;
        private readonly Dictionary<long, Hash256> _finalizedStateRoots = [];

        public long FinalizedBlockNumber => _finalizedBlockNumber;

        public void SetFinalizedBlockNumber(long blockNumber) => _finalizedBlockNumber = blockNumber;

        public void SetFinalizedStateRootAt(long blockNumber, Hash256 stateRoot) => _finalizedStateRoots[blockNumber] = stateRoot;

        public Hash256? GetFinalizedStateRootAt(long blockNumber) =>
            _finalizedStateRoots.TryGetValue(blockNumber, out Hash256? root) ? root : null;
    }

}
