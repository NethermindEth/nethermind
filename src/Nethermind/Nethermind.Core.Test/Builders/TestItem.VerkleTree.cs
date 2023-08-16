// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Interfaces;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;
using NUnit.Framework;

namespace Nethermind.Core.Test.Builders;

public static partial class TestItem
{
    public static readonly Stem Stem0 = new Stem("00000000000000000000000000000000000000000000000000000001101234");
    public static readonly Stem Stem1 = new Stem("00000000000000000000000000000000000000000000000000000001112345");
    public static readonly Stem Stem2 = new Stem("00000000000000000000000000000000000000000000000000000001113456");
    public static readonly Stem Stem3 = new Stem("00000000000000000000000000000000000000000000000000000001114567");
    public static readonly Stem Stem4 = new Stem("00000000000000000000000000000000000000000000000000000001123456");
    public static readonly Stem Stem5 = new Stem("00000000000000000000000000000000000000000000000000000001123457");

    public static readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
    public static readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;
    public static readonly Account _account2 = Build.An.Account.WithBalance(2).TestObject;
    public static readonly Account _account3 = Build.An.Account.WithBalance(3).TestObject;
    public static readonly Account _account4 = Build.An.Account.WithBalance(4).TestObject;
    public static readonly Account _account5 = Build.An.Account.WithBalance(5).TestObject;

    public static PathWithSubTree[] SubTreesWithPaths = new PathWithSubTree[]
    {
        new PathWithSubTree(Stem0, _account0.ToVerkleDict()),
        new PathWithSubTree(Stem1, _account1.ToVerkleDict()),
        new PathWithSubTree(Stem2, _account2.ToVerkleDict()),
        new PathWithSubTree(Stem3, _account3.ToVerkleDict()),
        new PathWithSubTree(Stem4, _account4.ToVerkleDict()),
        new PathWithSubTree(Stem5, _account5.ToVerkleDict()),
    };

    private static string GetDbPathForTest()
    {
        string tempDir = Path.GetTempPath();
        string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
        return Path.Combine(tempDir, dbname);
    }

    public static IVerkleTrieStore GetVerkleStore(DbMode dbMode, int history = 128)
    {
        IDbProvider provider;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                break;
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, GetDbPathForTest());
                break;
            case DbMode.ReadOnlyDb:
            default:
                throw new ArgumentOutOfRangeException(nameof(dbMode), dbMode, null);
        }

        return new VerkleStateStore(provider, LimboLogs.Instance,maxNumberOfBlocksInCache: history);
    }

    public static VerkleStateTree GetVerkleStateTree(IVerkleTrieStore? store)
    {
        store ??= GetVerkleStore(DbMode.MemDb);
        VerkleStateTree stateTree = new VerkleStateTree(store, LimboLogs.Instance);
        FillStateTreeWithTestAccounts(stateTree);
        return stateTree;
    }

    public static void FillStateTreeWithTestAccounts(VerkleStateTree stateTree)
    {
        stateTree.InsertStemBatch(Stem0, _account0.ToVerkleDict());
        stateTree.InsertStemBatch(Stem1, _account1.ToVerkleDict());
        stateTree.InsertStemBatch(Stem2, _account2.ToVerkleDict());
        stateTree.InsertStemBatch(Stem3, _account3.ToVerkleDict());
        stateTree.InsertStemBatch(Stem4, _account4.ToVerkleDict());
        stateTree.InsertStemBatch(Stem5, _account5.ToVerkleDict());
        stateTree.Commit();
        stateTree.CommitTree(0);
    }

    public static void InsertBigVerkleTree(VerkleTree tree, int numBlocks, long leafPerBlock, long pathPoolCount, out SortedDictionary<Pedersen, byte[]> leafs)
    {
        Pedersen[] pathPool = new Pedersen[pathPoolCount];
        leafs = new();

        for (int i = 0; i < pathPoolCount; i++)
        {
            byte[] key = new byte[32];
            ((UInt256)i).ToBigEndian(key);
            Pedersen keccak = new Pedersen(key);
            pathPool[i] = keccak;
        }


        for (int leafIndex = 0; leafIndex < 10000; leafIndex++)
        {
            byte[] value = new byte[32];
            Random.NextBytes(value);
            Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];
            tree.Insert(path, value);
            leafs[path] = value;
        }

        tree.Commit();
        tree.CommitTree(0);

        for (int blockNumber = 1; blockNumber <= numBlocks; blockNumber++)
        {
            for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
            {
                byte[] leafValue = new byte[32];



                Random.NextBytes(leafValue);
                Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];

                if (leafs.ContainsKey(path))
                {
                    if (!(Random.NextSingle() > 0.5)) continue;
                    Console.WriteLine($"blockNumber:{blockNumber} uKey:{path} uValue:{leafValue.ToHexString()}");
                    tree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                    Console.WriteLine("new values");
                }
                else
                {
                    Console.WriteLine($"blockNumber:{blockNumber} nKey:{path} nValue:{leafValue.ToHexString()}");
                    tree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                }
            }

            tree.Commit();
            tree.CommitTree(blockNumber);
        }
    }
}
