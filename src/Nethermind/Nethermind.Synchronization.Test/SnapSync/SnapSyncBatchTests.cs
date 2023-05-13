// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class SnapSyncBatchTests
{
    [Test]
    public void TestAccountRangeToString()
    {
        SnapSyncBatch batch = new()
        {
            AccountRangeRequest = new AccountRange(Keccak.Zero, Keccak.MaxValue, Keccak.Compute("abc"), 999)
        };

        batch.ToString().Should().Be("AccountRange: (999, 0x0000000000000000000000000000000000000000000000000000000000000000, 0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff, 0x4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45)");
    }

    [Test]
    public void TestStorageRangeToString()
    {
        SnapSyncBatch batch = new()
        {
            StorageRangeRequest = new StorageRange()
            {
                BlockNumber = 123,
                RootHash = Keccak.Zero,
                Accounts = new PathWithAccount[9],
                StartingHash = Keccak.MaxValue,
                LimitHash = Keccak.Compute("abc"),
            }
        };

        batch.ToString().Should().Be("StorageRange: (123, 0x0000000000000000000000000000000000000000000000000000000000000000, 0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff, 0x4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45)");
    }

    [Test]
    public void TestCodeRequestsToString()
    {
        SnapSyncBatch batch = new()
        {
            CodesRequest = new ValueKeccak[9],
        };

        batch.ToString().Should().Be("CodesRequest: (9)");
    }

    [Test]
    public void TestAccountToRefreshToString()
    {
        SnapSyncBatch batch = new()
        {
            AccountsToRefreshRequest = new AccountsToRefreshRequest()
            {
                RootHash = Keccak.Zero,
                Paths = new AccountWithStorageStartingHash[9],
            }
        };

        batch.ToString().Should().Be("AccountsToRefreshRequest: (0x0000000000000000000000000000000000000000000000000000000000000000, 9)");
    }
}
