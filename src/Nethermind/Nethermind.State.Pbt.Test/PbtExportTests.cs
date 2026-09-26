// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Steps;
using NSubstitute;
using NUnit.Framework;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtExportTests
{
    [Test]
    public void Step_runs_once_the_node_is_live()
    {
        RunnerStepDependenciesAttribute dependencies = (RunnerStepDependenciesAttribute)Attribute.GetCustomAttribute(
            typeof(ExportPbtImage), typeof(RunnerStepDependenciesAttribute))!;

        Assert.That(dependencies.Dependencies, Does.Contain(typeof(InitializeNetwork)),
            "an anchor ahead of the persisted state is only reachable once the node syncs");
    }

    [Test]
    public void Persist_target_is_unpinned_until_the_anchor_is_known([Values(0, 4)] int configured)
    {
        PbtExportPersistTarget target = new(new PbtConfig { ExportStepDistance = configured }, new FlatDbConfig { CompactSize = 32 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.TargetBlock, Is.Null);
            Assert.That(target.StepDistance, Is.EqualTo(configured == 0 ? 32UL : (ulong)configured));
        }

        target.PinTo(17);

        Assert.That(target.TargetBlock, Is.EqualTo(17UL));
    }

    /// <remarks>Flat keeps no history, so an anchor below the persisted state can never be produced; that has to
    /// fail before the export spends an hour scanning. Without any persisted state there is nothing to default to.</remarks>
    [Test]
    public void Refuses_an_anchor_the_flat_state_cannot_serve([Values("below-persisted", "nothing-persisted")] string invalid)
    {
        Harness harness = new() { Persisted = invalid == "below-persisted" ? new FlatStateId(100, Keccak.EmptyTreeHash.ValueHash256) : FlatStateId.PreGenesis };
        harness.Config.MigrationAnchor = invalid == "below-persisted" ? 99 : null;

        Assert.ThrowsAsync<InvalidConfigurationException>(() => harness.Step().Execute(CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Target.TargetBlock, Is.Null, "nothing is pinned when the anchor is refused");
            Assert.That(harness.PauseControl.ReceivedCalls(), Is.Empty);
        }
    }

    /// <remarks>Covers the ordering the export depends on: pin first so persistence stops on the anchor rather than
    /// the next boundary, wait for it to land, and only then pause so nothing accumulates under the reader. The
    /// reader here is hash-keyed, so the export itself refuses — after the sequence under test has run.</remarks>
    [Test]
    public void Pins_the_anchor_then_pauses_processing_once_it_is_persisted([Values] bool anchorAhead)
    {
        Harness harness = new() { Persisted = new FlatStateId(40, Keccak.EmptyTreeHash.ValueHash256) };
        harness.Config.MigrationAnchor = anchorAhead ? 42 : null;
        harness.ReachedState = new FlatStateId(anchorAhead ? 42UL : 40UL, Keccak.EmptyTreeHash.ValueHash256);
        if (anchorAhead) harness.PollsBeforeReaching = 2;

        Assert.ThrowsAsync<InvalidDataException>(() => harness.Step().Execute(CancellationToken.None),
            "a hash-keyed flat state carries no preimages to derive EIP-8297 keys from");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Target.TargetBlock, Is.EqualTo(anchorAhead ? 42UL : 40UL));
            harness.PauseControl.Received(1).Pause();
        }
    }

    private sealed class Harness
    {
        public PbtConfig Config { get; } = new();
        public PbtExportPersistTarget Target { get; }
        public IBlockProcessingPauseControl PauseControl { get; } = Substitute.For<IBlockProcessingPauseControl>();
        public required FlatStateId Persisted { get; init; }

        /// <summary>The state persistence lands on, or null when it never reaches the anchor.</summary>
        public FlatStateId? ReachedState { get; set; }

        /// <summary>Polls that still report <see cref="Persisted"/>, so the wait is actually exercised.</summary>
        public int PollsBeforeReaching { get; set; }

        private readonly IPersistenceManager _persistenceManager = Substitute.For<IPersistenceManager>();
        private readonly IBlockTree _blockTree = Substitute.For<IBlockTree>();

        public Harness()
        {
            Target = new PbtExportPersistTarget(Config, new FlatDbConfig());
            Config.MigrationExportPath = Path.Combine(Path.GetTempPath(), "pbt-export-" + Guid.NewGuid().ToString("N"));
        }

        public ExportPbtImage Step()
        {
            int polls = 0;
            _persistenceManager.GetCurrentPersistedStateId().Returns(_ =>
                ReachedState is { } reached && polls++ >= PollsBeforeReaching ? reached : Persisted);
            BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).TestObject;
            BlockHeader anchor = Build.A.BlockHeader.WithNumber((ulong)(Config.MigrationAnchor ?? 40)).TestObject;
            _blockTree.Genesis.Returns(genesis);
            _blockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(anchor);
            _blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);

            IPersistence flatPersistence = Substitute.For<IPersistence>();
            flatPersistence.CreateReader().Returns(Substitute.For<IPersistence.IPersistenceReader>());
            IDbFactory dbFactory = Substitute.For<IDbFactory>();
            dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(Path.GetTempPath());
            IDbProvider dbProvider = Substitute.For<IDbProvider>();
            dbProvider.CodeDb.Returns(new MemDb());

            return new ExportPbtImage(_persistenceManager, flatPersistence, Target, dbFactory, dbProvider, _blockTree,
                new ChainSpec { ChainId = 1 }, Config, PauseControl, Substitute.For<IProcessExitSource>(), LimboLogs.Instance)
            {
                PollInterval = TimeSpan.Zero
            };
        }
    }
}
