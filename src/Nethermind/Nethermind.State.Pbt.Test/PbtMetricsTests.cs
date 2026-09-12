// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Metric;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtMetricsTests
{
    private RecordingObserver _readOnlyBundleTime = null!;
    private IMetricObserver _originalReadOnlyBundleTime = null!;
    private RecordingObserver _writeBatchTime = null!;
    private RecordingObserver _rootHashTime = null!;
    private IMetricObserver _originalWriteBatchTime = null!;
    private IMetricObserver _originalRootHashTime = null!;
    private RecordingObserver _prepareLeafChangesTime = null!;
    private RecordingObserver _trieUpdaterTime = null!;
    private IMetricObserver _originalPrepareLeafChangesTime = null!;
    private IMetricObserver _originalTrieUpdaterTime = null!;

    [SetUp]
    public void Setup()
    {
        _originalReadOnlyBundleTime = Metrics.PbtReadOnlySnapshotBundleTimes;
        Metrics.PbtReadOnlySnapshotBundleTimes = _readOnlyBundleTime = new RecordingObserver();
        _originalWriteBatchTime = Metrics.PbtWriteBatchTime;
        _originalRootHashTime = Metrics.PbtRootHashTime;
        _originalPrepareLeafChangesTime = Metrics.PbtPrepareLeafChangesTime;
        _originalTrieUpdaterTime = Metrics.PbtTrieUpdaterTime;
        Metrics.PbtPrepareLeafChangesTime = _prepareLeafChangesTime = new RecordingObserver();
        Metrics.PbtTrieUpdaterTime = _trieUpdaterTime = new RecordingObserver();
        Metrics.PbtWriteBatchTime = _writeBatchTime = new RecordingObserver();
        Metrics.PbtRootHashTime = _rootHashTime = new RecordingObserver();
    }

    [TearDown]
    public void TearDown()
    {
        Metrics.PbtReadOnlySnapshotBundleTimes = _originalReadOnlyBundleTime;
        Metrics.PbtWriteBatchTime = _originalWriteBatchTime;
        Metrics.PbtRootHashTime = _originalRootHashTime;
        Metrics.PbtPrepareLeafChangesTime = _originalPrepareLeafChangesTime;
        Metrics.PbtTrieUpdaterTime = _originalTrieUpdaterTime;
    }

    /// <summary>Verifies commit timers, including skipping root timing when no fold occurs.</summary>
    [Test]
    public async Task CommittingABlock_TimesTheWriteBatchAndTheFoldsThatDidWork()
    {
        await using PbtTestContext ctx = new();
        PbtScopeProvider provider = ctx.CreateScopeProvider();

        using IWorldStateScopeProvider.IScope scope = provider.BeginScope(null, new LocalMetrics());

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(TestItem.AddressA, new Account(1, 100));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_writeBatchTime.Observations, Has.Count.EqualTo(1), "the batch is timed when it closes");
            Assert.That(_rootHashTime.Observations, Is.Empty, "nothing is folded until the root is asked for");
            Assert.That(_prepareLeafChangesTime.Observations, Is.Empty);
            Assert.That(_trieUpdaterTime.Observations, Is.Empty);
        }

        scope.UpdateRootHash();
        scope.UpdateRootHash();

        foreach (RecordingObserver observer in new[] { _rootHashTime, _prepareLeafChangesTime, _trieUpdaterTime })
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(observer.Observations, Has.Count.EqualTo(1), "a clean re-fold is not an observation");
                Assert.That(observer.Observations, Is.All.GreaterThan(0), "elapsed ticks, not a constant");
            }
        }
        Assert.That(_prepareLeafChangesTime.Observations[0] + _trieUpdaterTime.Observations[0],
            Is.LessThanOrEqualTo(_rootHashTime.Observations[0]), "phase timings are contained in the total");
    }

    [Test]
    public void PointReads_ReportOnlyTheAnsweringTier(
        [Values("snapshot", "tombstone", "selfdestruct", "persistence", "missing")] string scenario,
        [Values] bool detailedMetrics,
        [Values("", "0", "00", "01", "f", "ff")] string groupPath)
    {
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        ValueHash256 codeHash = TestItem.KeccakA.ValueHash256;
        PbtStorageFullKey storageKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtNodePath groupKey = new(Bytes.FromHexString(groupPath.PadRight((groupPath.Length + 1) / 2 * 2, '0')), groupPath.Length * 4);
        string partition = groupPath switch { "01" => "code", "f" or "ff" => "storage", _ => "account" };
        Account account = new(1, 100);
        EvmWord slot = EvmWordSlot.FromStripped(Bytes.FromHexString("01"));
        CodeInfo code = new(Bytes.FromHexString("6001"));
        using RefCountingMemory payload = PooledRefCountingMemoryProvider.Instance.Rent(1);
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        if (scenario != "missing")
        {
            reader.GetAccount(addressHash).Returns(account);
            reader.GetSlot(storageKey).Returns(slot);
            reader.GetCode(codeHash).Returns(code);
            reader.GetCodeReference(codeHash).Returns(1UL);
            reader.GetNodeGroup(groupKey).Returns(_ => { payload.AcquireLease(); return payload; });
        }

        using PbtSnapshotContent content = new();
        bool snapshotHit = scenario is "snapshot" or "tombstone" or "selfdestruct";
        bool deleted = scenario is "tombstone" or "selfdestruct";
        if (snapshotHit)
        {
            content.Accounts[addressHash] = deleted ? null : account;
            if (scenario == "selfdestruct") content.ClearStorage(addressHash);
            else content.Storages[storageKey] = deleted ? default : slot;
            content.SetCodeReference(codeHash, deleted ? null : 1UL);
            if (!deleted) payload.AcquireLease();
            content.NodeGroups[groupKey.ToPath<PbtStorageNodePath>()] = deleted ? null : payload;
            if (!deleted) content.Codes[codeHash] = code;
        }
        PbtSnapshotPooledList snapshots = new(2);
        IPbtResourcePool pool = Substitute.For<IPbtResourcePool>();
        snapshots.Add(new PbtSnapshot(StateId.PreGenesis, new StateId(0, default), default, content, pool, PbtResourcePool.Usage.MainBlockProcessing));
        using PbtSnapshotContent emptyContent = new();
        snapshots.Add(new PbtSnapshot(new StateId(0, default), new StateId(1, default), default, emptyContent, pool, PbtResourcePool.Usage.MainBlockProcessing));
        using PbtReadOnlySnapshotBundle bundle = new(snapshots, reader, detailedMetrics);

        Account? actualAccount = bundle.GetAccount(TestItem.AddressA);
        EvmWord actualSlot = bundle.GetSlot(TestItem.AddressA, 1);
        using RefCountingMemory? actualGroup = bundle.GetNodeGroup(groupKey);
        ulong actualReference = bundle.GetCodeReference(codeHash);
        CodeInfo? actualCode = bundle.GetCode(codeHash);

        string tier = snapshotHit ? "snapshot" : scenario == "missing" ? "persistence_null" : "persistence";
        string codeTier = deleted ? "persistence" : tier;
        bool empty = deleted || scenario == "missing";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualAccount, Is.EqualTo(empty ? null : account));
            Assert.That(actualSlot, Is.EqualTo(empty ? default : slot));
            Assert.That(actualGroup, Is.SameAs(empty ? null : payload));
            Assert.That(actualReference, Is.EqualTo(empty ? 0UL : 1UL));
            Assert.That(actualCode, Is.SameAs(scenario == "missing" ? null : code));
            Assert.That(_readOnlyBundleTime.Labels, Is.EqualTo(detailedMetrics
                ? new[] { $"account_{tier}", $"storage_{tier}", $"node_group_{partition}_{tier}", $"code_reference_{tier}", $"code_{codeTier}" }
                : []));
            Assert.That(_readOnlyBundleTime.Observations, Has.Count.EqualTo(detailedMetrics ? 5 : 0));
            Assert.That(_readOnlyBundleTime.Observations, Is.All.GreaterThanOrEqualTo(0));
        }
    }

    [Test]
    public async Task Manager_PropagatesDetailedMetricsToBundles([Values] bool detailedMetrics, [Values] bool preGenesis)
    {
        await using PbtTestContext ctx = new(metricsConfig: new MetricsConfig { EnableDetailedMetric = detailedMetrics });
        StateId state = StateId.PreGenesis;
        if (!preGenesis)
        {
            state = new StateId(0, default);
            PbtSnapshotContent content = ctx.ResourcePool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
            content.Accounts[PbtKeyDerivation.AddressKeyHash(TestItem.AddressA)] = new Account(1, 100);
            ctx.Repository.TryAdd(new PbtSnapshot(StateId.PreGenesis, state, default, content, ctx.ResourcePool, PbtResourcePool.Usage.MainBlockProcessing));
        }
        using PbtReadOnlySnapshotBundle bundle = ((IPbtDbManager)ctx.Manager).GatherReadOnlyBundle(state);
        bundle.GetAccount(TestItem.AddressA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_readOnlyBundleTime.Labels, Is.EqualTo(detailedMetrics
                ? new[] { preGenesis ? "account_persistence_null" : "account_snapshot" }
                : []));
            Assert.That(_readOnlyBundleTime.Observations, Has.Count.EqualTo(detailedMetrics ? 1 : 0));
            Assert.That(_readOnlyBundleTime.Observations, Is.All.GreaterThanOrEqualTo(0));
        }
    }

    private sealed class RecordingObserver : IMetricObserver
    {
        public List<double> Observations { get; } = [];

        public List<string> Labels { get; } = [];

        public void Observe(double value, IMetricLabels? labels = null)
        {
            Observations.Add(value);
            if (labels is not null) Labels.Add(labels.Labels[0]);
        }
    }
}
