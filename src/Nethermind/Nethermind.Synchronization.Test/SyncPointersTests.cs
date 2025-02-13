// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Test;
using Nethermind.Db;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class SyncPointersTests
{
    [Test]
    public void WhenReceiptNotStore_SetLowestInsertedReceiptTo0()
    {
        SyncPointers pointers = new SyncPointers(new TestMemDb(), new TestMemColumnsDb<ReceiptsColumns>(),
            Substitute.For<IBlockTree>(),
            new SyncConfig(),
            new ReceiptConfig()
            {
                StoreReceipts = false
            });

        pointers.LowestInsertedReceiptBlockNumber.Should().Be(0);
    }
}
