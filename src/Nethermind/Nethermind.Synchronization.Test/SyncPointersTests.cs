// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Test;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class SyncPointersTests
{
    [Test]
    public void WhenReceiptNotStore_SetLowestInsertedReceiptTo0()
    {
        SyncPointers pointers = new SyncPointers(new TestMemDb(), new TestMemColumnsDb<ReceiptsColumns>(), new ReceiptConfig()
        {
            StoreReceipts = false
        });

        pointers.LowestInsertedReceiptBlockNumber.Should().Be(0);
    }
}
