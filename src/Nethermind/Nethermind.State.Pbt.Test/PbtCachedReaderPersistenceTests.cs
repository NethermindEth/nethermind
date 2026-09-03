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
    private static readonly ValueHash256 _committedRoot = TestItem.KeccakB.ValueHash256;

    /// <summary>Readers of the same persisted state share a snapshot until a commit invalidates it.</summary>
    [Test]
    public async Task Readers_ShareOneSnapshot_UntilAWriteBatchCommits()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        using IPbtPersistence.IReader first = persistence.CreateReader();
        using IPbtPersistence.IReader second = persistence.CreateReader();

        Assert.That(second, Is.SameAs(first));
        ctx.Inner.Received(1).CreateReader();

        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None).Dispose();

        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(first));
        ctx.Inner.Received(2).CreateReader();
    }

    /// <summary>Readers retain a snapshot after its cache entry is invalidated.</summary>
    [Test]
    public async Task Snapshot_IsClosed_OnlyOnceTheCacheAndEveryReaderReleasedIt()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IReader stillReading = persistence.CreateReader();
        persistence.CreateReader().Dispose();

        ctx.Reader.DidNotReceive().Dispose();

        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None).Dispose();

        ctx.Reader.DidNotReceive().Dispose();

        stillReading.Dispose();

        ctx.Reader.Received(1).Dispose();
    }

    /// <summary>Readers use the prepared snapshot during a batch, which is refreshed only after the batch completes.</summary>
    [Test]
    public async Task Snapshot_IsPreparedBeforeTheWriteBatch_AndRefreshedAfterItCompletes()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);

        Received.InOrder(() =>
        {
            ctx.Inner.CreateReader();
            ctx.Inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>());
        });

        using IPbtPersistence.IReader duringBatch = persistence.CreateReader();
        using IPbtPersistence.IReader alsoDuringBatch = persistence.CreateReader();

        Assert.That(alsoDuringBatch, Is.SameAs(duringBatch));
        ctx.Inner.Received(1).CreateReader();

        ctx.Batch.ClearReceivedCalls();
        ctx.Inner.ClearReceivedCalls();
        batch.Dispose();

        Received.InOrder(() =>
        {
            ctx.Batch.Dispose();
            ctx.Inner.CreateReader();
        });
        ctx.Inner.Received(1).CreateReader();

        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(duringBatch));
        ctx.Inner.Received(1).CreateReader();
    }

    /// <summary>A snapshot becomes stale only after all writing batches close.</summary>
    [Test]
    public async Task OverlappingWriteBatches_HoldTheSnapshot_UntilTheLastOneCloses()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IWriteBatch first = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);
        IPbtPersistence.IWriteBatch second = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);

        using IPbtPersistence.IReader pinned = persistence.CreateReader();
        first.Dispose();

        using (IPbtPersistence.IReader stillPinned = persistence.CreateReader()) Assert.That(stillPinned, Is.SameAs(pinned));
        ctx.Inner.Received(1).CreateReader();

        second.Dispose();
        ctx.Inner.Received(2).CreateReader();

        using IPbtPersistence.IReader afterLastCommit = persistence.CreateReader();

        Assert.That(afterLastCommit, Is.Not.SameAs(pinned));
        ctx.Inner.Received(2).CreateReader();
    }

    [Test]
    public async Task WriteBatch_ThatThrowsOnDispose_StillRefreshesTheSnapshot()
    {
        Context ctx = new();
        ctx.Batch.When(static batch => batch.Dispose()).Do(static _ => throw new InvalidOperationException("flush failed"));
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        using IPbtPersistence.IReader beforeCommit = persistence.CreateReader();
        IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);

        Assert.That(() => batch.Dispose(), Throws.InvalidOperationException);
        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(beforeCommit));
        ctx.Inner.Received(2).CreateReader();
    }

    /// <summary>Disposing an unclaimed batch releases its cache pin.</summary>
    [Test]
    public async Task WriteBatch_ThatFailedToOpen_LeavesTheSnapshotInvalidatable()
    {
        Context ctx = new();
        ctx.Inner.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
            .Throws(new InvalidOperationException("wrong base state"));

        await using PbtCachedReaderPersistence persistence = ctx.Build();

        Assert.That(() => persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, _committedRoot, WriteFlags.None),
            Throws.InvalidOperationException);

        using IPbtPersistence.IReader beforeCommit = persistence.CreateReader();
        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None).Dispose();

        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(beforeCommit));
    }

    [Test]
    public async Task SharedReader_ForwardsToTheSnapshotUnderneath()
    {
        Context ctx = new();
        PbtFullKey key = PbtStateKey.Account(TestItem.AddressA, PbtKeyDerivation.BasicDataLeafKey);
        ValueHash256 value = TestItem.KeccakB.ValueHash256;
        ctx.Reader.CurrentState.Returns(_committedState);
        ctx.Reader.CurrentRoot.Returns(_committedRoot);
        ctx.Reader.GetLeaf(key).Returns(value);

        await using PbtCachedReaderPersistence persistence = ctx.Build();
        using IPbtPersistence.IReader reader = persistence.CreateReader();

        Assert.That(reader.CurrentState, Is.EqualTo(_committedState));
        Assert.That(reader.CurrentRoot, Is.EqualTo(_committedRoot));
        Assert.That(reader.GetLeaf(key), Is.EqualTo(value));
    }

    private sealed class Context
    {
        public IPbtPersistence Inner { get; } = Substitute.For<IPbtPersistence>();

        public IPbtPersistence.IReader Reader { get; } = Substitute.For<IPbtPersistence.IReader>();

        public IPbtPersistence.IWriteBatch Batch { get; } = Substitute.For<IPbtPersistence.IWriteBatch>();

        public Context()
        {
            // Return distinct snapshots so cache invalidation remains observable.
            Inner.CreateReader().Returns(_ => Reader, _ => Substitute.For<IPbtPersistence.IReader>());
            Inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
                .Returns(Batch);
        }

        public PbtCachedReaderPersistence Build() => new(Inner, Substitute.For<IProcessExitSource>());
    }
}
