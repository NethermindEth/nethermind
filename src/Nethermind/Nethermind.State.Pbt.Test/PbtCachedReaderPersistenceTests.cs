// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtCachedReaderPersistenceTests
{
    private static readonly StateId _committedState = new(1, TestItem.KeccakA);
    private static readonly ValueHash256 _committedRoot = TestItem.KeccakB.ValueHash256;

    /// <summary>Readers retain a snapshot after its cache entry is invalidated.</summary>
    [Test]
    public async Task Snapshot_IsClosed_OnlyOnceTheCacheAndEveryReaderReleasedIt()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IReader stillReading = persistence.CreateReader();
        persistence.CreateReader().Dispose();

        ctx.Reader.DidNotReceive().Dispose();

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None))
            batch.Commit();

        ctx.Reader.DidNotReceive().Dispose();

        stillReading.Dispose();

        ctx.Reader.Received(1).Dispose();
    }

    /// <summary>
    /// Readers use the prepared snapshot until Commit publishes the new state, and every overlapping batch
    /// holds its own cache pin until disposed.
    /// </summary>
    [Test]
    public async Task Snapshot_IsPreparedBeforeTheWriteBatch_RefreshedImmediatelyAfterCommit_AndPinnedByEachOpenBatch()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);

        Received.InOrder(() =>
        {
            ctx.Inner.CreateReader();
            ctx.Inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>());
        });

        IPbtPersistence.IWriteBatch second = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);
        using IPbtPersistence.IReader duringBatch = persistence.CreateReader();
        using IPbtPersistence.IReader alsoDuringBatch = persistence.CreateReader();

        Assert.That(alsoDuringBatch, Is.SameAs(duringBatch));
        ctx.Inner.Received(1).CreateReader();

        ctx.Inner.ClearReceivedCalls();
        batch.Commit();
        using IPbtPersistence.IReader afterCommitBeforeDispose = persistence.CreateReader();

        Assert.That(afterCommitBeforeDispose, Is.Not.SameAs(duringBatch));
        ctx.Inner.Received(1).CreateReader();

        batch.Dispose();
        using IPbtPersistence.IReader afterDispose = persistence.CreateReader();

        Assert.That(afterDispose, Is.SameAs(afterCommitBeforeDispose));
        ctx.Inner.Received(1).CreateReader();

        persistence.ClearCaches();
        using IPbtPersistence.IReader whileSecondIsOpen = persistence.CreateReader();

        Assert.That(whileSecondIsOpen, Is.SameAs(afterCommitBeforeDispose));

        second.Dispose();
        persistence.ClearCaches();
        using IPbtPersistence.IReader afterBothDispose = persistence.CreateReader();

        Assert.That(afterBothDispose, Is.Not.SameAs(afterCommitBeforeDispose));
        ctx.Inner.Received(2).CreateReader();
    }

    [Test]
    public async Task RepeatedCommitAndDispose_PublishAndReleaseOnlyOnce()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);
        batch.Commit();
        batch.Commit();
        batch.Dispose();
        batch.Dispose();

        ctx.Batch.Received(1).Commit();
        ctx.Batch.Received(1).Dispose();
        ctx.Inner.Received(1).CreateReader();
    }

    [Test]
    public async Task DisposingUncommittedBatch_DoesNotRefreshTheSnapshot()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        using IPbtPersistence.IReader beforeAbort = persistence.CreateReader();
        persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None).Dispose();
        using IPbtPersistence.IReader afterAbort = persistence.CreateReader();

        Assert.That(afterAbort, Is.SameAs(beforeAbort));
        ctx.Batch.DidNotReceive().Commit();
        ctx.Inner.Received(1).CreateReader();
    }

    [Test]
    public async Task FailedCommit_DoesNotRefreshTheSnapshot_AndDisposeReleasesThePin()
    {
        Context ctx = new();
        ctx.Batch.When(static batch => batch.Commit()).Do(static _ => throw new InvalidOperationException("commit failed"));
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        using IPbtPersistence.IReader beforeCommit = persistence.CreateReader();
        IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);

        Assert.That(() => batch.Commit(), Throws.InvalidOperationException);
        using IPbtPersistence.IReader afterFailedCommit = persistence.CreateReader();
        Assert.That(afterFailedCommit, Is.SameAs(beforeCommit));

        batch.Dispose();
        using IPbtPersistence.IReader afterDispose = persistence.CreateReader();
        Assert.That(afterDispose, Is.SameAs(beforeCommit));
        ctx.Inner.Received(1).CreateReader();
    }

    /// <summary>A write batch that fails to open releases its cache pin, so the snapshot can still be cleared.</summary>
    [Test]
    public async Task WriteBatch_ThatFailedToOpen_LeavesTheSnapshotInvalidatable()
    {
        Context ctx = new();
        ctx.Inner.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
            .Throws(new InvalidOperationException("wrong base state"));

        await using PbtCachedReaderPersistence persistence = ctx.Build();

        Assert.That(() => persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, _committedRoot, WriteFlags.None),
            Throws.InvalidOperationException);

        using IPbtPersistence.IReader beforeClear = persistence.CreateReader();
        persistence.ClearCaches();

        using IPbtPersistence.IReader afterClear = persistence.CreateReader();

        Assert.That(afterClear, Is.Not.SameAs(beforeClear));
    }

    [Test]
    public async Task Reader_and_batch_forward_to_the_inner_persistence()
    {
        Context ctx = new();
        ValueHash256 key = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        Account value = new(7, 9);
        PbtNodePath groupKey = new([], 0);
        using RefCountingMemory payload = RefCountingMemory.OwningRocksDb(new ArrayMemoryManager(new byte[PbtNodeGroupCodec.MaxTrailerLength]));
        ctx.Reader.CurrentState.Returns(_committedState);
        ctx.Reader.CurrentRoot.Returns(_committedRoot);
        ctx.Reader.GetAccount(key).Returns(value.ToPbtAccount());
        ctx.Reader.GetNodeGroup(groupKey).Returns(_ =>
        {
            payload.AcquireLease();
            return payload;
        });

        await using PbtCachedReaderPersistence persistence = ctx.Build();
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using RefCountingMemory lease = reader.GetNodeGroup(groupKey)!;
        using IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);

        batch.SetNodeGroup(groupKey, payload);
        batch.SetNodeGroup(groupKey, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(_committedState));
            Assert.That(reader.CurrentRoot, Is.EqualTo(_committedRoot));
            Assert.That(reader.GetAccount(key), Is.EqualTo(value.ToPbtAccount()));
            Assert.That(lease, Is.SameAs(payload));
        }
        ctx.Batch.Received(1).SetNodeGroup(groupKey, payload);
        ctx.Batch.Received(1).SetNodeGroup(groupKey, null);
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
