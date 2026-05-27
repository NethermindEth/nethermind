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
    private object _writeLock = null!;
    private Exception _commitFailure = null!;
    private IWriteBatch _writeBatch = null!;
    private BatchWrite _batch = null!;

    [SetUp]
    public void SetUp()
    {
        _writeLock = new object();
        _commitFailure = new Exception("commit failed");
        _writeBatch = Substitute.For<IWriteBatch>();
        _writeBatch.When(static b => b.Dispose()).Do(_ => throw _commitFailure);
        _batch = new BatchWrite(_writeLock, _writeBatch);
    }

    [Test]
    public void Dispose_WhenWriteBatchThrows_ReleasesLockAndMarksDisposed()
    {
        Assert.Throws<Exception>(() => _batch.Dispose())
            .Should().BeSameAs(_commitFailure, "the original commit failure must propagate");
        _batch.Disposed.Should().BeTrue("a failed commit must still mark the batch disposed");

        Action exitOnceMore = () => Monitor.Exit(_writeLock);
        exitOnceMore.Should().Throw<SynchronizationLockException>(
            "the write lock must be released, so this thread no longer owns it after a failed commit");
    }

    [Test]
    public void Dispose_WhenCalledAgainAfterFailure_DoesNotReuseFailedBatch()
    {
        Assert.Throws<Exception>(() => _batch.Dispose());

        Action secondDispose = () => _batch.Dispose();
        secondDispose.Should().NotThrow("a disposed batch must not re-enter the failed commit");
        _writeBatch.Received(1).Dispose();
    }
}
