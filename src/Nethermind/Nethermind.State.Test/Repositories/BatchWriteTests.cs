// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test.Repositories;

public class BatchWriteTests
{
    [Test]
    public void Dispose_WhenWriteBatchThrows_ReleasesLockAndMarksDisposed()
    {
        (BatchWrite batch, _, object writeLock, Exception failure) = CreateFailingBatch();

        Exception thrown = Assert.Throws<Exception>(() => batch.Dispose())!;
        Assert.That(thrown, Is.SameAs(failure), "the original commit failure must propagate");
        Assert.That(batch.Disposed, Is.True, "a failed commit must still mark the batch disposed");

        Assert.That(() => Monitor.Exit(writeLock), Throws.TypeOf<SynchronizationLockException>(),
            "the write lock must be released, so this thread no longer owns it after a failed commit");
    }

    [Test]
    public void Dispose_WhenCalledAgainAfterFailure_DoesNotReuseFailedBatch()
    {
        (BatchWrite batch, IWriteBatch writeBatch, _, _) = CreateFailingBatch();

        Assert.That(() => batch.Dispose(), Throws.TypeOf<Exception>(), "the failed commit must surface on first dispose");
        Assert.That(() => batch.Dispose(), Throws.Nothing, "a disposed batch must not re-enter the failed commit");
        writeBatch.Received(1).Dispose();
    }

    [Test]
    public void Constructor_WhenFactoryThrows_ReleasesLock()
    {
        object writeLock = new();
        Exception failure = new("factory failed");

        Assert.That(() => new BatchWrite(writeLock, () => throw failure), Throws.Exception.SameAs(failure));
        Assert.That(() => Monitor.Exit(writeLock), Throws.TypeOf<SynchronizationLockException>(),
            "the write lock must be released when the factory fails — otherwise it would deadlock all future writers");
    }

    private static (BatchWrite Batch, IWriteBatch WriteBatch, object WriteLock, Exception Failure) CreateFailingBatch()
    {
        object writeLock = new();
        Exception failure = new("commit failed");
        IWriteBatch writeBatch = Substitute.For<IWriteBatch>();
        writeBatch.When(static b => b.Dispose()).Do(_ => throw failure);
        return (new BatchWrite(writeLock, () => writeBatch), writeBatch, writeLock, failure);
    }
}
