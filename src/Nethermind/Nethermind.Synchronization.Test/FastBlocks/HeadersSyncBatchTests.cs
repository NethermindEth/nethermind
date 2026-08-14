// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.FastBlocks;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastBlocks;

public class HeadersSyncBatchTests
{
    [Test]
    public void TestMinNumberIsCorrect()
    {
        HeadersSyncBatch batch = new()
        {
            StartNumber = 10,
            RequestSize = 20,
        };

        Assert.That(batch.EndNumber, Is.EqualTo(29));
        Assert.That(batch.MinNumber, Is.EqualTo(29));
    }
}
