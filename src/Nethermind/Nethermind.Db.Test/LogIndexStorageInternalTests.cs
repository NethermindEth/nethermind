// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Db.Test;

public class LogIndexStorageInternalTests
{
    [TestCase(new[] { 1 })]
    [TestCase(new[] { 1, 2, 3 })]
    [TestCase(new[] { 1, 2, 3, 4, 7, 8, 9 })]
    [TestCase(new[] { 1, 100, 200, 999 })]
    public void CompressDecompressDbValue(int[] blockNums)
    {
        var dbValue = LogIndexStorage.CreateDbValue(blockNums);
        var compressed = LogIndexStorage.CompressDbValue(dbValue);
        var decompressed = LogIndexStorage.DecompressDbValue(compressed);
        Assert.That(decompressed, Is.EquivalentTo(blockNums));
    }
}
