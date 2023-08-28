// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.VerkleSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.VerkleSync;

public class VerkleProgressTrackerTest
{
    [Test]
    public void Will_create_multiple_get_address_range_request()
    {
        BlockTree blockTree = Build.A.BlockTree().WithBlocks(Build.A.Block.TestObject).TestObject;
        VerkleProgressTracker progressTracker = new(blockTree, new MemDb(), LimboLogs.Instance, 4);

        (VerkleSyncBatch request, bool finished) = progressTracker.GetNextRequest();
        request.SubTreeRangeRequest.Should().NotBeNull();
        request.SubTreeRangeRequest!.StartingStem.Bytes[0].Should().Be(0);
        request.SubTreeRangeRequest.LimitStem!.Bytes[0].Should().Be(64);
        finished.Should().BeFalse();

        (request, finished) = progressTracker.GetNextRequest();
        request.SubTreeRangeRequest.Should().NotBeNull();
        request.SubTreeRangeRequest!.StartingStem.Bytes[0].Should().Be(64);
        request.SubTreeRangeRequest.LimitStem!.Bytes[0].Should().Be(128);
        finished.Should().BeFalse();

        (request, finished) = progressTracker.GetNextRequest();
        request.SubTreeRangeRequest.Should().NotBeNull();
        request.SubTreeRangeRequest!.StartingStem.Bytes[0].Should().Be(128);
        request.SubTreeRangeRequest.LimitStem!.Bytes[0].Should().Be(192);
        finished.Should().BeFalse();

        (request, finished) = progressTracker.GetNextRequest();
        request.SubTreeRangeRequest.Should().NotBeNull();
        request.SubTreeRangeRequest!.StartingStem.Bytes[0].Should().Be(192);
        request.SubTreeRangeRequest.LimitStem!.Bytes[0].Should().Be(255);
        finished.Should().BeFalse();

        (request, finished) = progressTracker.GetNextRequest();
        request.Should().BeNull();
        finished.Should().BeFalse();
    }
}
