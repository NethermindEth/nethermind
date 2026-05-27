// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using FluentAssertions;
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

        Assert.Throws<Exception>(() => batch.Dispose())
            .Should().BeSameAs(failure, "the original commit failure must propagate");
        batch.Disposed.Should().BeTrue("a failed commit must still mark the batch disposed");

        Action exitOnceMore = () => Monitor.Exit(writeLock);
        exitOnceMore.Should().Throw<SynchronizationLockException>(
            "the write lock must be released, so this thread no longer owns it after a failed commit");
    }

    [Test]
    public void Dispose_WhenCalledAgainAfterFailure_DoesNotReuseFailedBatch()
    {
        (BatchWrite batch, IWriteBatch writeBatch, _, _) = CreateFailingBatch();

        Assert.Throws<Exception>(() => batch.Dispose());

        Action secondDispose = () => batch.Dispose();
        secondDispose.Should().NotThrow("a disposed batch must not re-enter the failed commit");
        writeBatch.Received(1).Dispose();
    }

    private static (BatchWrite Batch, IWriteBatch WriteBatch, object WriteLock, Exception Failure) CreateFailingBatch()
    {
        object writeLock = new();
        Exception failure = new("commit failed");
        IWriteBatch writeBatch = Substitute.For<IWriteBatch>();
        writeBatch.When(static b => b.Dispose()).Do(_ => throw failure);
        return (new BatchWrite(writeLock, writeBatch), writeBatch, writeLock, failure);
    }
}
