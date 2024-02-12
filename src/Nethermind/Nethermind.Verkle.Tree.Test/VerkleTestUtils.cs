// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.TreeStore;
using NUnit.Framework;

namespace Nethermind.Verkle.Tree.Test;

public static class VerkleTestUtils
{
    public static readonly byte[] Array1To32 =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    };
    public static readonly byte[] Array1To32Last128 =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 128,
    };
    public static readonly byte[] EmptyArray =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };
    public static readonly byte[] ArrayAll1 =
    {
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1
    };
    public static readonly byte[] ArrayAll3 =
    {
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3
    };

    public static readonly byte[] SplitKeyTest =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 72, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] Start40Key =
    {
        40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] MaxValue =
        Convert.FromHexString("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    public static readonly byte[] ArrayAll0Last1 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
    };

    public static readonly byte[] ArrayAll0Last2 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2
    };

    public static readonly byte[] ArrayAll0Last3 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3
    };

    public static readonly byte[] ArrayAll0Last4 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4
    };

    public static readonly byte[] ValueEmptyCodeHashValue =
    {
        197, 210, 70, 1, 134, 247, 35, 60, 146, 126, 125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39, 59, 123, 250, 216, 4, 93, 133, 164, 112,
    };

    public static readonly byte[] StartWith1 = new byte[]
    {
        1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] StartWith2 = new byte[]
    {
        2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] StartWith3 = new byte[]
    {
        3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly Hash256 KeyVersion = (Hash256)new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 0,
    };

    public static readonly Hash256 KeyBalance = (Hash256)new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 1,
    };

    public static readonly Hash256 KeyNonce = (Hash256) new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 2,
    };

    public static readonly Hash256 KeyCodeCommitment = (Hash256)new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 3,
    };

    public static readonly Hash256 KeyCodeSize =(Hash256) new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 4,
    };

    public static string GetDbPathForTest()
    {
        string tempDir = Path.GetTempPath();
        string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
        return Path.Combine(tempDir, dbname);
    }

    public static IVerkleTreeStore GetVerkleStoreForTest<TCache>(DbMode dbMode)
    where TCache: struct, IPersistenceStrategy
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

        return new VerkleTreeStore<TCache>(provider, LimboLogs.Instance);
    }

    public static VerkleTree GetVerkleTreeForTest<TCache>(DbMode dbMode, string? path = null)
    where TCache: struct, IPersistenceStrategy
    {
        IDbProvider provider;
        IVerkleTreeStore store;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                store = new VerkleTreeStore<TCache>(provider, LimboLogs.Instance);
                return new VerkleTree(store, LimboLogs.Instance);
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, path ?? GetDbPathForTest());
                store = new VerkleTreeStore<TCache>(provider, LimboLogs.Instance);
                return new VerkleTree(store, LimboLogs.Instance);
            case DbMode.ReadOnlyDb:
            default:
                throw new ArgumentOutOfRangeException(nameof(dbMode), dbMode, null);
        }
    }

    public static VerkleTree GetFilledVerkleTreeForTest(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);

        tree.Insert(KeyVersion, EmptyArray);
        tree.Insert(KeyBalance, EmptyArray);
        tree.Insert(KeyNonce, EmptyArray);
        tree.Insert(KeyCodeCommitment, ValueEmptyCodeHashValue);
        tree.Insert(KeyCodeSize, EmptyArray);
        tree.Commit();
        tree.CommitTree(0);

        tree.Get(KeyVersion).Should().BeEquivalentTo(EmptyArray);
        tree.Get(KeyBalance).Should().BeEquivalentTo(EmptyArray);
        tree.Get(KeyNonce).Should().BeEquivalentTo(EmptyArray);
        tree.Get(KeyCodeCommitment).Should().BeEquivalentTo(ValueEmptyCodeHashValue);
        tree.Get(KeyCodeSize).Should().BeEquivalentTo(EmptyArray);

        tree.Insert(KeyVersion, ArrayAll0Last2);
        tree.Insert(KeyBalance, ArrayAll0Last2);
        tree.Insert(KeyNonce, ArrayAll0Last2);
        tree.Insert(KeyCodeCommitment, ValueEmptyCodeHashValue);
        tree.Insert(KeyCodeSize, ArrayAll0Last2);
        tree.Commit();
        tree.CommitTree(1);

        tree.Get(KeyVersion).Should().BeEquivalentTo(ArrayAll0Last2);
        tree.Get(KeyBalance).Should().BeEquivalentTo(ArrayAll0Last2);
        tree.Get(KeyNonce).Should().BeEquivalentTo(ArrayAll0Last2);
        tree.Get(KeyCodeCommitment).Should().BeEquivalentTo(ValueEmptyCodeHashValue);
        tree.Get(KeyCodeSize).Should().BeEquivalentTo(ArrayAll0Last2);

        tree.Insert(KeyVersion, ArrayAll0Last3);
        tree.Insert(KeyBalance, ArrayAll0Last3);
        tree.Insert(KeyNonce, ArrayAll0Last3);
        tree.Insert(KeyCodeCommitment, ValueEmptyCodeHashValue);
        tree.Insert(KeyCodeSize, ArrayAll0Last3);
        tree.Commit();
        tree.CommitTree(2);

        tree.Get(KeyVersion).Should().BeEquivalentTo(ArrayAll0Last3);
        tree.Get(KeyBalance).Should().BeEquivalentTo(ArrayAll0Last3);
        tree.Get(KeyNonce).Should().BeEquivalentTo(ArrayAll0Last3);
        tree.Get(KeyCodeCommitment).Should().BeEquivalentTo(ValueEmptyCodeHashValue);
        tree.Get(KeyCodeSize).Should().BeEquivalentTo(ArrayAll0Last3);

        tree.Insert(KeyVersion, ArrayAll0Last4);
        tree.Insert(KeyBalance, ArrayAll0Last4);
        tree.Insert(KeyNonce, ArrayAll0Last4);
        tree.Insert(KeyCodeCommitment, ValueEmptyCodeHashValue);
        tree.Insert(KeyCodeSize, ArrayAll0Last4);
        tree.Commit();
        tree.CommitTree(3);

        tree.Get(KeyVersion).Should().BeEquivalentTo(ArrayAll0Last4);
        tree.Get(KeyBalance).Should().BeEquivalentTo(ArrayAll0Last4);
        tree.Get(KeyNonce).Should().BeEquivalentTo(ArrayAll0Last4);
        tree.Get(KeyCodeCommitment).Should().BeEquivalentTo(ValueEmptyCodeHashValue);
        tree.Get(KeyCodeSize).Should().BeEquivalentTo(ArrayAll0Last4);

        return tree;
    }

    public static VerkleTree CreateVerkleTreeWithKeysAndValues(byte[][] keys, byte[][] values)
    {
        VerkleTree tree = GetVerkleTreeForTest<VerkleSyncCache>(DbMode.MemDb);
        for (int i = 0; i < keys.Length; i++)
        {
            tree.Insert((Hash256)keys[i], values[i]);
        }
        tree.Commit();
        tree.CommitTree(0);
        return tree;
    }
}
