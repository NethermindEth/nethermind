// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtCachedReaderPersistenceTests
{
    private static readonly StateId _committedState = new(1, TestItem.KeccakA);
    private static readonly ValueHash256 _committedTreeRoot = TestItem.KeccakB;

    /// <summary>
    /// The point of the cache: everyone reading the same persisted state gets the same snapshot, and
    /// the commit that makes it stale is what takes it away.
    /// </summary>
    [Test]
    public async Task Readers_ShareOneSnapshot_UntilAWriteBatchCommits()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        using IPbtPersistence.IReader first = persistence.CreateReader();
        using IPbtPersistence.IReader second = persistence.CreateReader();

        Assert.That(second, Is.SameAs(first));
        ctx.Inner.Received(1).CreateReader();

        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedTreeRoot, WriteFlags.None).Dispose();

        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(first));
        ctx.Inner.Received(2).CreateReader();
    }

    /// <summary>The snapshot outlives the cache entry: whoever is still reading it keeps it open.</summary>
    [Test]
    public async Task Snapshot_IsClosed_OnlyOnceTheCacheAndEveryReaderReleasedIt()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IReader stillReading = persistence.CreateReader();
        persistence.CreateReader().Dispose();

        ctx.Reader.DidNotReceive().Dispose();

        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedTreeRoot, WriteFlags.None).Dispose();

        ctx.Reader.DidNotReceive().Dispose();

        stillReading.Dispose();

        ctx.Reader.Received(1).Dispose();
    }

    /// <summary>
    /// The columns are not written atomically on every backend, so the snapshot everyone reads has to
    /// predate the batch and outlast it: a reader taken while it is open must never take one of its own.
    /// </summary>
    [Test]
    public async Task Snapshot_IsTakenBeforeTheWriteBatch_AndSharedForItsLifetime()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedTreeRoot, WriteFlags.None);

        Received.InOrder(() =>
        {
            ctx.Inner.CreateReader();
            ctx.Inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>());
        });

        using IPbtPersistence.IReader duringBatch = persistence.CreateReader();
        using IPbtPersistence.IReader alsoDuringBatch = persistence.CreateReader();

        Assert.That(alsoDuringBatch, Is.SameAs(duringBatch));
        ctx.Inner.Received(1).CreateReader();

        batch.Dispose();

        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(duringBatch));
        ctx.Inner.Received(2).CreateReader();
    }

    /// <summary>The snapshot is only stale once every batch that could still be writing has closed.</summary>
    [Test]
    public async Task OverlappingWriteBatches_HoldTheSnapshot_UntilTheLastOneCloses()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IWriteBatch first = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedTreeRoot, WriteFlags.None);
        IPbtPersistence.IWriteBatch second = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedTreeRoot, WriteFlags.None);

        using IPbtPersistence.IReader pinned = persistence.CreateReader();
        first.Dispose();

        using (IPbtPersistence.IReader stillPinned = persistence.CreateReader()) Assert.That(stillPinned, Is.SameAs(pinned));

        second.Dispose();

        using IPbtPersistence.IReader afterLastCommit = persistence.CreateReader();

        Assert.That(afterLastCommit, Is.Not.SameAs(pinned));
    }

    /// <summary>
    /// A batch that never gets handed out is never disposed, so the pin it took has to come back off
    /// here - a leaked one would wedge the cache on a snapshot that never goes stale.
    /// </summary>
    [Test]
    public async Task WriteBatch_ThatFailedToOpen_LeavesTheSnapshotInvalidatable()
    {
        Context ctx = new();
        ctx.Inner.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
            .Throws(new InvalidOperationException("wrong base state"));

        await using PbtCachedReaderPersistence persistence = ctx.Build();

        Assert.That(() => persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, _committedTreeRoot, WriteFlags.None),
            Throws.InvalidOperationException);

        using IPbtPersistence.IReader beforeCommit = persistence.CreateReader();
        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedTreeRoot, WriteFlags.None).Dispose();

        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(beforeCommit));
    }

    [Test]
    public async Task SharedReader_ForwardsToTheSnapshotUnderneath()
    {
        Context ctx = new();
        Stem stem = PbtKeyDerivation.AccountHeaderStem(TestItem.AddressA);
        RefCountingMemory blob = RefCountingMemory.Wrapping([0x11]);
        ctx.Reader.CurrentState.Returns(_committedState);
        ctx.Reader.CurrentTreeRoot.Returns(_committedTreeRoot);
        ctx.Reader.GetLeafBlob(stem).Returns(blob);

        await using PbtCachedReaderPersistence persistence = ctx.Build();
        using IPbtPersistence.IReader reader = persistence.CreateReader();

        Assert.That(reader.CurrentState, Is.EqualTo(_committedState));
        Assert.That(reader.CurrentTreeRoot, Is.EqualTo(_committedTreeRoot));
        Assert.That(reader.GetLeafBlob(stem), Is.SameAs(blob));
    }

    private sealed class Context
    {
        public IPbtPersistence Inner { get; } = Substitute.For<IPbtPersistence>();

        public IPbtPersistence.IReader Reader { get; } = Substitute.For<IPbtPersistence.IReader>();

        public Context()
        {
            // a fresh substitute per call after the first, so "is this the same snapshot?" stays a
            // meaningful question however many times the cache is invalidated
            Inner.CreateReader().Returns(_ => Reader, _ => Substitute.For<IPbtPersistence.IReader>());
            Inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
                .Returns(Substitute.For<IPbtPersistence.IWriteBatch>());
        }

        public PbtCachedReaderPersistence Build() => new(Inner, Substitute.For<IProcessExitSource>());
    }
}
