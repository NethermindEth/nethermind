// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.Verkle.Tree.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class GenesisTestBench
{
    [TearDown]
    public void CleanTestData()
    {
        string dbPath = VerkleTestUtils.GetDbPathForTest();
        if (Directory.Exists(dbPath))
        {
            Directory.Delete(dbPath, true);
        }
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertKey0Value0(DbMode dbMode)
    {
        using var reader = new StreamReader("/home/eurus/nethermind/src/Nethermind/Nethermind.Verkle.Tree.Test/genesis.csv");
        var listA = new List<Hash256>();
        var listB = new List<byte[]>();
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            var values = line.Split(',');

            listA.Add(new Hash256(Bytes.FromHexString(values[0])));
            listB.Add(Bytes.FromHexString(values[1]));
        }

        // for (int j = 0; j < 100; j++)
        // {
        //     VerkleTree treeXx = VerkleTestUtils.GetVerkleTreeForTest<PersistEveryBlock>(dbMode);
        //     treeXx.Commit();
        //
        //     for (int i = 0; i < listA.Count; i++)
        //     {
        //         treeXx.Insert(listA[i], listB[i]);
        //     }
        //     treeXx.Commit();
        // }


        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<PersistEveryBlock>(dbMode);

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < listA.Count; i++)
        {
            tree.Insert(listA[i], listB[i]);
        }

        var timeElapsedInsert = watch.ElapsedMilliseconds;
        Console.WriteLine($"timeElapsedInsert:{timeElapsedInsert}");
        watch = Stopwatch.StartNew();
        tree.Commit();
        var timeElapsedCommit = watch.ElapsedMilliseconds;
        Console.WriteLine($"timeElapsedCommit:{timeElapsedCommit}");

        AssertRootHash(tree.StateRoot.Bytes,
            "5e8519756841faf0b2c28951c451b61a4b407b70a5ce5b57992f4bec973173ff");
    }

    private static void AssertRootHash(Span<byte> realRootHash, string expectedRootHash)
    {
        Convert.ToHexString(realRootHash).Should()
            .BeEquivalentTo(expectedRootHash.ToUpper());
    }

}
