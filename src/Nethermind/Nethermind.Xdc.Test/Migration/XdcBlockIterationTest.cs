// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;
using Xdc;
using static Nethermind.Core.BlockHeader.Format;

namespace Nethermind.Xdc.Test.Migration;

public class XdcBlockIterationTest
{
    private static void IterateBackward(string dbPath, Func<XdcReader, XdcBlockHeader, bool> callback)
    {
        using var db = new ReadOnlyLevelDb(dbPath);
        var reader = new XdcReader(db);

        XdcBlockHeader? header = reader.GetHeadHeader();
        Assert.That(header, Is.Not.Null, "Failed to decode latest header");

        int filteredCount = 0;

        TestContext.Out.WriteLine($"Starting at block {header.ToString(FullHashAndNumber)}\n");

        XdcBlockHeader? lastHeader = null;
        while (header is not null)
        {
            if (callback(reader, header))
                filteredCount++;

            if (header.ParentHash is null || header.ParentHash == Keccak.Zero)
                break;

            (UInt256? prevTD, UInt256 diff) = (header.TotalDifficulty, header.Difficulty);

            header = reader.GetHeader(header.ParentHash, includeTD: true);

            UInt256? nextTD = header?.TotalDifficulty;

            if (prevTD is not null && nextTD is not null)
                Assert.That(nextTD, Is.EqualTo(prevTD - diff));

            if (header is not null)
                lastHeader = header;
        }

        TestContext.Out.WriteLine($"\nIteration complete, last block: {lastHeader?.ToString(FullHashAndNumber)}");
        Assert.That(filteredCount, Is.GreaterThan(0));
    }

    [TestCase(@"D:\Nethermind\xdc\chaindata")]
    public void IterateBackwardEpochSwitch(string dbPath)
    {
        IterateBackward(dbPath, (_, header) =>
        {
            if (header.Validators is null || header.Validators.Length <= 0)
                return false;

            TestContext.Out.WriteLine($"Epoch switch block: {header.ToString(FullHashAndNumber)}, Validators: [{header.ValidatorsAddress!.Value.Length}]");
            return true;
        });
    }

    [TestCase(@"D:\Nethermind\xdc\chaindata")]
    public void IterateBackwardSnapshots(string dbPath)
    {
        IterateBackward(dbPath, (reader, header) =>
        {
            if (reader.GetSnapshotAt(header) is not {} snap)
                return false;

            TestContext.Out.WriteLine($"Snapshot @{snap.BlockNumber}, Header: {snap.HeaderHash}, Candidates: [{snap.NextEpochCandidates.Length}]");
            return true;
        });
    }
}
