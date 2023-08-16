using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Utils;
using NUnit.Framework;

namespace Nethermind.Verkle.Tree.Test;


[TestFixture, Parallelizable(ParallelScope.All)]
public class VerkleTreeTests
{
    private readonly byte[] _array1To32 =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    };
    private readonly byte[] _array1To32Last128 =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 128,
    };
    private readonly byte[] _emptyArray =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };
    private readonly byte[] _arrayAll1 =
    {
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1
    };

    private readonly byte[] _arrayAll0Last2 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2
    };

    private readonly byte[] _arrayAll0Last3 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3
    };

    private readonly byte[] _arrayAll0Last4 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4
    };


    private readonly Pedersen _keyVersion = new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 0,
    };

    private readonly Pedersen _keyBalance = new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 1,
    };

    private readonly Pedersen _keyNonce = new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 2,
    };

    private readonly Pedersen _keyCodeCommitment = new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 3,
    };

    private readonly Pedersen _keyCodeSize = new byte[]
    {
        121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229, 224,
        183, 72, 25, 6, 8, 210, 159, 31, 4,
    };

    private readonly byte[] _valueEmptyCodeHashValue =
    {
        197, 210, 70, 1, 134, 247, 35, 60, 146, 126, 125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39, 59,
        123, 250, 216, 4, 93, 133, 164, 112,
    };

    private static string GetDbPathForTest()
    {
        string tempDir = Path.GetTempPath();
        string dbname = $"VerkleTrie_TestID_{TestContext.CurrentContext.Test.ID}";
        return Path.Combine(tempDir, dbname);
    }

    private static VerkleTree GetVerkleTreeForTest(DbMode dbMode)
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

    private VerkleTree GetFilledVerkleTreeForTest(DbMode dbMode)
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

    [TearDown]
    public void CleanTestData()
    {
        string dbPath = GetDbPathForTest();
        if (Directory.Exists(dbPath))
        {
            Directory.Delete(dbPath, true);
        }
    }


    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertKey0Value0(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        byte[] key = _emptyArray;

        tree.Insert(key, key);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6B630905CE275E39F223E175242DF2C1E8395E6F46EC71DCE5557012C1334A5C");

        tree.Get(key).Should().BeEquivalentTo(key);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertKey1Value1(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        byte[] key = _array1To32;

        tree.Insert(key, key);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6F5E7CFC3A158A64E5718B0D2F18F564171342380F5808F3D2A82F7E7F3C2778");

        tree.Get(key).Should().BeEquivalentTo(key);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertSameStemTwoLeaves(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        byte[] keyA = _array1To32;

        byte[] keyB = _array1To32Last128;

        tree.Insert(keyA, keyA);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6F5E7CFC3A158A64E5718B0D2F18F564171342380F5808F3D2A82F7E7F3C2778");
        tree.Insert(keyB, keyB);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "14EE5E5C5B698E363055B41DD3334F8168C7FCA4F85C5E30AB39CF9CC2FEEF70");

        tree.Get(keyA).Should().BeEquivalentTo(keyA);
        tree.Get(keyB).Should().BeEquivalentTo(keyB);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertKey1Val1Key2Val2(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        byte[] keyA = _emptyArray;
        byte[] keyB = _arrayAll1;

        tree.Insert(keyA, keyA);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6B630905CE275E39F223E175242DF2C1E8395E6F46EC71DCE5557012C1334A5C");
        tree.Insert(keyB, keyB);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "5E208CDBA664A7B8FBDC26A1C1185F153A5F721CBA389625C18157CEF7D4931C");

        tree.Get(keyA).Should().BeEquivalentTo(keyA);
        tree.Get(keyB).Should().BeEquivalentTo(keyB);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertLongestPath(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        byte[] keyA = _emptyArray;
        byte[] keyB = (byte[])_emptyArray.Clone();
        keyB[30] = 1;

        tree.Insert(keyA, keyA);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6B630905CE275E39F223E175242DF2C1E8395E6F46EC71DCE5557012C1334A5C");
        tree.Insert(keyB, keyB);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "3258D722AEA34B5AE7CB24A9B0175EDF0533C651FA09592E823B5969C729FB88");

        tree.Get(keyA).Should().BeEquivalentTo(keyA);
        tree.Get(keyB).Should().BeEquivalentTo(keyB);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertAndTraverseLongestPath(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        byte[] keyA = _emptyArray;
        tree.Insert(keyA, keyA);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6B630905CE275E39F223E175242DF2C1E8395E6F46EC71DCE5557012C1334A5C");

        byte[] keyB = (byte[])_emptyArray.Clone();
        keyB[30] = 1;
        tree.Insert(keyB, keyB);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "3258D722AEA34B5AE7CB24A9B0175EDF0533C651FA09592E823B5969C729FB88");

        byte[] keyC = (byte[])_emptyArray.Clone();
        keyC[29] = 1;
        tree.Insert(keyC, keyC);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "5B82B26A1A7E00A1E997ABD51FE3075D05F54AA4CB1B3A70607E62064FADAA82");

        tree.Get(keyA).Should().BeEquivalentTo(keyA);
        tree.Get(keyB).Should().BeEquivalentTo(keyB);
        tree.Get(keyC).Should().BeEquivalentTo(keyC);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestEmptyTrie(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        tree.Commit();
        tree.StateRoot.Bytes.Should().BeEquivalentTo(FrE.Zero.ToBytes().ToArray());
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestSimpleUpdate(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        byte[] key = _array1To32;
        byte[] value = _emptyArray;
        tree.Insert(key, value);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "140A25B322EAA1ADACD0EE1BB135ECA7B78FCF02B4B19E4A55B26B7A434F42AC");
        tree.Get(key).Should().BeEquivalentTo(value);

        tree.Insert(key, key);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6F5E7CFC3A158A64E5718B0D2F18F564171342380F5808F3D2A82F7E7F3C2778");
        tree.Get(key).Should().BeEquivalentTo(key);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGet(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);

        tree.Insert(_keyVersion, _emptyArray);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "476C50753A22B270DA9D409C0F9AB655AB2506CE4EF831481DD455F0EA730FEF");

        tree.Insert(_keyBalance, _arrayAll0Last2);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6D9E4F2D418DE2822CE9C2F4193C0E155E4CAC6CF4170E44098DC49D4B571B7B");

        tree.Insert(_keyNonce, _emptyArray);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "5C7AE53FE2AAE9852127140C1E2F5122BB3759A7975C0E7A1AEC7CAF7C711FDE");

        tree.Insert(_keyCodeCommitment, _valueEmptyCodeHashValue);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "3FD5FA25042DB0304792BFC007514DA5B777516FFBDAA5658AF36D355ABD9BD8");

        tree.Insert(_keyCodeSize, _emptyArray);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "006BD679A8204502DCBF9A002F0B828AECF5A29A3A5CE454E651E3A96CC02FE2");

        tree.Get(_keyVersion).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyBalance).Should().BeEquivalentTo(_arrayAll0Last2);
        tree.Get(_keyNonce).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyCodeCommitment).Should().BeEquivalentTo(_valueEmptyCodeHashValue);
        tree.Get(_keyCodeSize).Should().BeEquivalentTo(_emptyArray);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestValueSameBeforeAndAfterFlush(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);


        tree.Insert(_keyVersion, _emptyArray);
        tree.Insert(_keyBalance, _emptyArray);
        tree.Insert(_keyNonce, _emptyArray);
        tree.Insert(_keyCodeCommitment, _valueEmptyCodeHashValue);
        tree.Insert(_keyCodeSize, _emptyArray);

        tree.Get(_keyVersion).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyBalance).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyNonce).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyCodeCommitment).Should().BeEquivalentTo(_valueEmptyCodeHashValue);
        tree.Get(_keyCodeSize).Should().BeEquivalentTo(_emptyArray);

        tree.Commit();
        tree.CommitTree(0);

        tree.Get(_keyVersion).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyBalance).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyNonce).Should().BeEquivalentTo(_emptyArray);
        tree.Get(_keyCodeCommitment).Should().BeEquivalentTo(_valueEmptyCodeHashValue);
        tree.Get(_keyCodeSize).Should().BeEquivalentTo(_emptyArray);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGetMultiBlock(DbMode dbMode)
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
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestBeverlyHillGenesis(DbMode dbMode)
    {
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        byte[][] keys =
        {
            new byte[]
            {
                80, 91, 197, 250, 186, 158, 22, 244, 39, 111, 133, 220, 198, 184, 196, 37, 196, 170, 83, 13, 248, 137, 214, 145, 207, 141, 22, 250, 127, 178, 242, 98
            },
            new byte[]
            {
                80, 91, 197, 250, 186, 158, 22, 244, 39, 111, 133, 220, 198, 184, 196, 37, 196, 170, 83, 13, 248, 137, 214, 145, 207, 141, 22, 250, 127, 178, 242, 0
            },
            new byte[]
            {
                80, 91, 197, 250, 186, 158, 22, 244, 39, 111, 133, 220, 198, 184, 196, 37, 196, 170, 83, 13, 248, 137, 214, 145, 207, 141, 22, 250, 127, 178, 242, 1
            },
            new byte[]
            {
                80, 91, 197, 250, 186, 158, 22, 244, 39, 111, 133, 220, 198, 184, 196, 37, 196, 170, 83, 13, 248, 137, 214, 145, 207, 141, 22, 250, 127, 178, 242, 2
            },
            new byte[]
            {
                80, 91, 197, 250, 186, 158, 22, 244, 39, 111, 133, 220, 198, 184, 196, 37, 196, 170, 83, 13, 248, 137, 214, 145, 207, 141, 22, 250, 127, 178, 242, 3
            },
            new byte[]
            {
                80, 28, 126, 51, 3, 54, 20, 30, 142, 44, 127, 93, 139, 146, 112, 200, 182, 35, 165, 99, 140, 74, 215, 203, 100, 29, 142, 136, 89, 75, 19, 0
            },
            new byte[]
            {
                80, 28, 126, 51, 3, 54, 20, 30, 142, 44, 127, 93, 139, 146, 112, 200, 182, 35, 165, 99, 140, 74, 215, 203, 100, 29, 142, 136, 89, 75, 19, 1
            },
            new byte[]
            {
                80, 28, 126, 51, 3, 54, 20, 30, 142, 44, 127, 93, 139, 146, 112, 200, 182, 35, 165, 99, 140, 74, 215, 203, 100, 29, 142, 136, 89, 75, 19, 2
            },
            new byte[]
            {
                80, 28, 126, 51, 3, 54, 20, 30, 142, 44, 127, 93, 139, 146, 112, 200, 182, 35, 165, 99, 140, 74, 215, 203, 100, 29, 142, 136, 89, 75, 19, 3
            }

        };
        byte[][] values =
        {
            new byte[]
            {
                245, 165, 253, 66, 209, 106, 32, 48, 39, 152, 239, 110, 211, 9, 151, 155, 67, 0, 61, 35, 32, 217, 240, 232, 234, 152, 49, 169, 39, 89, 251, 75
            },
            new byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            },
            new byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            },
            new byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            },
            new byte[]
            {
                197, 210, 70, 1, 134, 247, 35, 60, 146, 126, 125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39, 59, 123, 250, 216, 4, 93, 133, 164, 112
            },
            new byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            },
            new byte[]
            {
                1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            },
            new byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            },
            new byte[]
            {
                197, 210, 70, 1, 134, 247, 35, 60, 146, 126, 125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39, 59, 123, 250, 216, 4, 93, 133, 164, 112
            },

        };
        byte[][] expectedRootHash =
        {
            new byte[]
            {
                65, 218, 34, 122, 247, 60, 5, 240, 251, 52, 246, 140, 37, 216, 19, 241, 119, 101, 100, 139, 61, 137, 58, 14, 46, 134, 177, 141, 32, 154, 1, 59
            },
            new byte[]
            {
                69, 56, 80, 6, 24, 134, 42, 30, 244, 7, 143, 117, 127, 102, 1, 223, 77, 33, 172, 250, 47, 138, 224, 44, 49, 218, 223, 138, 225, 174, 75, 206
            },
            new byte[]
            {
                1, 193, 213, 250, 133, 201, 192, 237, 111, 140, 199, 105, 38, 248, 215, 48, 91, 252, 209, 7, 111, 242, 47, 75, 91, 142, 31, 125, 12, 184, 18, 245
            },
            new byte[]
            {
                81, 28, 164, 25, 6, 44, 54, 172, 110, 118, 89, 173, 112, 198, 226, 209, 24, 221, 92, 3, 221, 62, 85, 11, 85, 179, 58, 178, 78, 187, 238, 71
            },
            new byte[]
            {
                57, 217, 114, 85, 250, 173, 204, 43, 147, 248, 170, 130, 74, 146, 15, 162, 181, 125, 23, 235, 3, 110, 20, 156, 89, 185, 169, 139, 69, 112, 37, 175
            },
            new byte[]
            {
                3, 81, 31, 1, 31, 195, 243, 62, 60, 100, 13, 77, 215, 21, 9, 92, 237, 104, 172, 25, 234, 240, 207, 142, 94, 213, 237, 176, 195, 163, 39, 87
            },
            new byte[]
            {
                83, 211, 141, 144, 197, 72, 206, 46, 100, 220, 68, 98, 90, 239, 120, 251, 113, 102, 18, 170, 98, 238, 138, 174, 249, 120, 142, 238, 79, 192, 75, 59
            },
            new byte[]
            {
                91, 25, 255, 166, 55, 136, 143, 224, 38, 101, 85, 250, 65, 108, 59, 161, 105, 247, 140, 181, 185, 207, 120, 76, 92, 218, 72, 103, 18, 0, 214, 144
            },
            new byte[]
            {
                5, 153, 156, 66, 226, 161, 228, 136, 209, 248, 42, 235, 222, 221, 118, 125, 87, 137, 13, 106, 11, 70, 26, 248, 3, 21, 218, 184, 201, 164, 194, 198
            }

        };


        for (int i = 0; i < keys.Length; i++)
        {
            tree.Insert(keys[i], values[i]);
            tree.Commit();
            AssertRootHash(tree.StateRoot.Bytes, expectedRootHash[i]);
        }
    }

    private static void AssertRootHash(byte[] realRootHash, string expectedRootHash)
    {
        Convert.ToHexString(realRootHash).Should()
            .BeEquivalentTo(expectedRootHash);
    }

    private static void AssertRootHash(IEnumerable<byte> realRootHash, IEnumerable<byte> expectedRootHash)
    {
        realRootHash.Should().BeEquivalentTo(expectedRootHash);
    }
}
