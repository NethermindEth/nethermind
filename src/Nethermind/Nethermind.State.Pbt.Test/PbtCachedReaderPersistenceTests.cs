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

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None))
            batch.Commit();

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

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None))
            batch.Commit();

        ctx.Reader.DidNotReceive().Dispose();

        stillReading.Dispose();

        ctx.Reader.Received(1).Dispose();
    }

    /// <summary>Readers use the prepared snapshot until Commit publishes the new state.</summary>
    [Test]
    public async Task Snapshot_IsPreparedBeforeTheWriteBatch_AndRefreshedImmediatelyAfterCommit()
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

        ctx.Inner.ClearReceivedCalls();
        batch.Commit();
        using IPbtPersistence.IReader afterCommitBeforeDispose = persistence.CreateReader();

        Assert.That(afterCommitBeforeDispose, Is.Not.SameAs(duringBatch));
        ctx.Inner.Received(1).CreateReader();

        batch.Dispose();
        using IPbtPersistence.IReader afterDispose = persistence.CreateReader();

        Assert.That(afterDispose, Is.SameAs(afterCommitBeforeDispose));
        ctx.Inner.Received(1).CreateReader();
    }

    /// <summary>Overlapping batches release cache pins independently without changing publication visibility.</summary>
    [Test]
    public async Task OverlappingWriteBatches_RefreshOnCommit_AndReleasePinsIndependently()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();

        IPbtPersistence.IWriteBatch first = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);
        IPbtPersistence.IWriteBatch second = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);

        using IPbtPersistence.IReader pinned = persistence.CreateReader();
        first.Commit();
        using IPbtPersistence.IReader afterFirstCommit = persistence.CreateReader();

        Assert.That(afterFirstCommit, Is.Not.SameAs(pinned));

        first.Dispose();
        second.Dispose();
        using IPbtPersistence.IReader afterBothDispose = persistence.CreateReader();

        Assert.That(afterBothDispose, Is.SameAs(afterFirstCommit));
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
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None))
            batch.Commit();

        using IPbtPersistence.IReader afterCommit = persistence.CreateReader();

        Assert.That(afterCommit, Is.Not.SameAs(beforeCommit));
    }

    [Test]
    public async Task Staging_write_does_not_publish_or_refresh_the_cached_reader()
    {
        Context ctx = new();
        await using PbtCachedReaderPersistence persistence = ctx.Build();
        using IPbtPersistence.IReader beforeStaging = persistence.CreateReader();

        persistence.CreateStagingWriteBatch(WriteFlags.None).Dispose();

        using IPbtPersistence.IReader afterStaging = persistence.CreateReader();
        Assert.That(afterStaging, Is.SameAs(beforeStaging));
        ctx.Inner.Received(1).CreateReader();
        ctx.Inner.Received(1).CreateStagingWriteBatch(WriteFlags.None);
    }

    [Test]
    public async Task SharedReader_ForwardsToTheSnapshotUnderneath()
    {
        Context ctx = new();
        ValueHash256 key = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        Account value = new(7, 9);
        ctx.Reader.CurrentState.Returns(_committedState);
        ctx.Reader.CurrentRoot.Returns(_committedRoot);
        ctx.Reader.GetAccount(key).Returns(value);

        await using PbtCachedReaderPersistence persistence = ctx.Build();
        using IPbtPersistence.IReader reader = persistence.CreateReader();

        Assert.That(reader.CurrentState, Is.EqualTo(_committedState));
        Assert.That(reader.CurrentRoot, Is.EqualTo(_committedRoot));
        Assert.That(reader.GetAccount(key), Is.EqualTo(value));
    }

    [Test]
    public async Task Group_operations_forward_borrowed_payloads_and_owned_read_leases()
    {
        Context ctx = new();
        PbtNodePath groupKey = new([], 0);
        using RefCountingMemory payload = RefCountingMemory.Wrapping(new byte[PbtNodeGroupCodec.MaxTrailerLength]);
        ctx.Reader.GetNodeGroup(groupKey).Returns(_ =>
        {
            payload.AcquireLease();
            return payload;
        });
        ctx.Reader.EnumerateNodeGroupKeys().Returns(new PbtStorageNodePath[] { groupKey.ToPath<PbtStorageNodePath>() });
        await using PbtCachedReaderPersistence persistence = ctx.Build();
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        using RefCountingMemory lease = reader.GetNodeGroup(groupKey)!;
        using IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, _committedState, _committedRoot, WriteFlags.None);

        batch.SetNodeGroup(groupKey, payload);
        batch.SetNodeGroup(groupKey, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lease, Is.SameAs(payload));
            Assert.That(reader.EnumerateNodeGroupKeys(), Is.EqualTo(new[] { groupKey.ToPath<PbtStorageNodePath>() }));
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
            Inner.CreateStagingWriteBatch(Arg.Any<WriteFlags>()).Returns(Batch);
        }

        public PbtCachedReaderPersistence Build() => new(Inner, Substitute.For<IProcessExitSource>());
    }
}
