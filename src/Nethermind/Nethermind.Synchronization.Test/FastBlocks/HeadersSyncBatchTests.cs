// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Synchronization.FastBlocks;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastBlocks;

public class HeadersSyncBatchTests
{
    [Test]
    public void TestMinNumberIsCorrect()
    {
        HeadersSyncBatch batch = new HeadersSyncBatch()
        {
            StartNumber = 10,
            RequestSize = 20,
        };

        batch.EndNumber.Should().Be(29);
        batch.MinNumber.Should().Be(29);
    }
}
