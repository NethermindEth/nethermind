// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Synchronization.VerkleSync;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.VerkleSync;

public class VerkleSyncBatchTests
{
    [Test]
    public void TestAccountRangeToString()
    {
        VerkleSyncBatch batch = new()
        {
            SubTreeRangeRequest = new SubTreeRange(Pedersen.Zero, Stem.MaxValue.Bytes, Keccak.Compute("abc").Bytes[..31].ToArray(), 999)
        };

        Console.WriteLine(batch.ToString());
        batch.ToString().Should().Be("SubTreeRange: (999, 0x0000000000000000000000000000000000000000000000000000000000000000, 0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff, 0x4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c)");
    }
}
