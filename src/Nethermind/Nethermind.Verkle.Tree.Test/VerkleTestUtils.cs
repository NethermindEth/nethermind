// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Verkle.Tree;
using NUnit.Framework;

namespace Nethermind.Verkle.Tree.Test;

public static class VerkleTestUtils
{
    public static readonly byte[] _array1To32 =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    };
    public static readonly byte[] _array1To32Last128 =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 128,
    };
    public static readonly byte[] _emptyArray =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };
    public static readonly byte[] _arrayAll1 =
    {
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1
    };
    public static readonly byte[] _arrayAll3 =
    {
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3
    };

    public static readonly byte[] _splitKeyTest =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 72, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] _start40Key =
    {
        40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] _maxValue =
        Convert.FromHexString("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    public static readonly byte[] _arrayAll0Last1 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
    };

    public static readonly byte[] _arrayAll0Last2 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2
    };

    public static readonly byte[] _arrayAll0Last3 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3
    };

    public static readonly byte[] _arrayAll0Last4 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4
    };


    public static readonly byte[] _keyVersion =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224, 183, 72, 25, 6, 8, 210, 159, 31, 0,
    };
    public static readonly byte[] _keyBalance =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224, 183, 72, 25, 6, 8, 210, 159, 31, 1,
    };
    public static readonly byte[] _keyNonce =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224, 183, 72, 25, 6, 8, 210, 159, 31, 2,
    };
    public static readonly byte[] _keyCodeCommitment = {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224, 183, 72, 25, 6, 8, 210, 159, 31, 3,
    };
    public static readonly byte[] _keyCodeSize =
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224, 183, 72, 25, 6, 8, 210, 159, 31, 4,
    };
    public static readonly byte[] _valueEmptyCodeHashValue =
    {
        197, 210, 70, 1, 134, 247, 35, 60, 146, 126, 125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39, 59, 123, 250, 216, 4, 93, 133, 164, 112,
    };

    public static readonly byte[] _startWith1 = new byte[]
    {
        1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] _startWith2 = new byte[]
    {
        2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] _startWith3 = new byte[]
    {
        3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    public static string GetDbPathForTest()
    {
        string tempDir = Path.GetTempPath();
        string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
        return Path.Combine(tempDir, dbname);
    }

    public static VerkleTree GetVerkleTreeForTest(DbMode dbMode)
    {
        IDbProvider provider;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                return new VerkleTree(provider, LimboLogs.Instance);
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, GetDbPathForTest());
                return new VerkleTree(provider, LimboLogs.Instance);
            case DbMode.ReadOnlyDb:
            default:
                throw new ArgumentOutOfRangeException(nameof(dbMode), dbMode, null);
        }
    }

    public static VerkleTree GetFilledVerkleTreeForTest(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);

        tree.Insert(_keyVersion, _emptyArray);
        tree.Insert(_keyBalance, _emptyArray);
        tree.Insert(_keyNonce, _emptyArray);
        tree.Insert(_keyCodeCommitment, _valueEmptyCodeHashValue);
        tree.Insert(_keyCodeSize, _emptyArray);
        tree.Commit();
        tree.CommitTree(0);

        tree.Get(_keyVersion).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyBalance).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyNonce).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyCodeCommitment).Should().BeEquivalentTo(_valueEmptyCodeHashValue);
        tree.Get(_keyCodeSize).Should().BeEquivalentTo(_emptyArray);

        tree.Insert(_keyVersion, _arrayAll0Last2);
        tree.Insert(_keyBalance, _arrayAll0Last2);
        tree.Insert(_keyNonce, _arrayAll0Last2);
        tree.Insert(_keyCodeCommitment, _valueEmptyCodeHashValue);
        tree.Insert(_keyCodeSize, _arrayAll0Last2);
        tree.Commit();
        tree.CommitTree(1);

        tree.Get(_keyVersion).Should().BeEquivalentTo(_arrayAll0Last2);
        tree.Get(_keyBalance).Should().BeEquivalentTo(_arrayAll0Last2);
        tree.Get(_keyNonce).Should().BeEquivalentTo(_arrayAll0Last2);
        tree.Get(_keyCodeCommitment).Should().BeEquivalentTo(_valueEmptyCodeHashValue);
        tree.Get(_keyCodeSize).Should().BeEquivalentTo(_arrayAll0Last2);

        tree.Insert(_keyVersion, _arrayAll0Last3);
        tree.Insert(_keyBalance, _arrayAll0Last3);
        tree.Insert(_keyNonce, _arrayAll0Last3);
        tree.Insert(_keyCodeCommitment, _valueEmptyCodeHashValue);
        tree.Insert(_keyCodeSize, _arrayAll0Last3);
        tree.Commit();
        tree.CommitTree(2);

        tree.Get(_keyVersion).Should().BeEquivalentTo(_arrayAll0Last3);
        tree.Get(_keyBalance).Should().BeEquivalentTo(_arrayAll0Last3);
        tree.Get(_keyNonce).Should().BeEquivalentTo(_arrayAll0Last3);
        tree.Get(_keyCodeCommitment).Should().BeEquivalentTo(_valueEmptyCodeHashValue);
        tree.Get(_keyCodeSize).Should().BeEquivalentTo(_arrayAll0Last3);

        tree.Insert(_keyVersion, _arrayAll0Last4);
        tree.Insert(_keyBalance, _arrayAll0Last4);
        tree.Insert(_keyNonce, _arrayAll0Last4);
        tree.Insert(_keyCodeCommitment, _valueEmptyCodeHashValue);
        tree.Insert(_keyCodeSize, _arrayAll0Last4);
        tree.Commit();
        tree.CommitTree(3);

        tree.Get(_keyVersion).Should().BeEquivalentTo(_arrayAll0Last4);
        tree.Get(_keyBalance).Should().BeEquivalentTo(_arrayAll0Last4);
        tree.Get(_keyNonce).Should().BeEquivalentTo(_arrayAll0Last4);
        tree.Get(_keyCodeCommitment).Should().BeEquivalentTo(_valueEmptyCodeHashValue);
        tree.Get(_keyCodeSize).Should().BeEquivalentTo(_arrayAll0Last4);

        return tree;
    }

    public static VerkleTree CreateVerkleTreeWithKeysAndValues(byte[][] keys, byte[][] values)
    {
        VerkleTree tree = GetVerkleTreeForTest(DbMode.MemDb);
        for (int i = 0; i < keys.Length; i++)
        {
            tree.Insert(keys[i], values[i]);
        }
        tree.Commit();
        tree.CommitTree(0);
        return tree;
    }
}
