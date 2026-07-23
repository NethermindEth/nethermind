// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Metric;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtMetricsTests
{
    private RecordingObserver _writeBatchTime = null!;
    private RecordingObserver _rootHashTime = null!;
    private IMetricObserver _originalWriteBatchTime = null!;
    private IMetricObserver _originalRootHashTime = null!;

    [SetUp]
    public void Setup()
    {
        _originalWriteBatchTime = Metrics.PbtWriteBatchTime;
        _originalRootHashTime = Metrics.PbtRootHashTime;
        Metrics.PbtWriteBatchTime = _writeBatchTime = new RecordingObserver();
        Metrics.PbtRootHashTime = _rootHashTime = new RecordingObserver();
    }

    [TearDown]
    public void TearDown()
    {
        Metrics.PbtWriteBatchTime = _originalWriteBatchTime;
        Metrics.PbtRootHashTime = _originalRootHashTime;
    }

    /// <summary>
    /// Both timers are only useful if they fire on the ordinary commit path, and the root one only if
    /// it skips the folds that had nothing to fold.
    /// </summary>
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

        Assert.That(_writeBatchTime.Observations, Has.Count.EqualTo(1), "the batch is timed when it closes");
        Assert.That(_rootHashTime.Observations, Is.Empty, "nothing is folded until the root is asked for");

        scope.UpdateRootHash();
        scope.UpdateRootHash();

        Assert.That(_rootHashTime.Observations, Has.Count.EqualTo(1), "a clean re-fold is not an observation");
        Assert.That(_rootHashTime.Observations[0], Is.GreaterThan(0), "elapsed ticks, not a constant");
    }

    private sealed class RecordingObserver : IMetricObserver
    {
        public List<double> Observations { get; } = [];

        public void Observe(double value, IMetricLabels? labels = null) => Observations.Add(value);
    }
}
