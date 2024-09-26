using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.Utils;
using NUnit.Framework;

namespace Nethermind.Verkle.Tree.Test;


[TestFixture, Parallelizable(ParallelScope.All)]
public class VerkleTreeTests
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
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        byte[] key = VerkleTestUtils.EmptyArray;

        tree.Insert((Hash256)key, key);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6B630905CE275E39F223E175242DF2C1E8395E6F46EC71DCE5557012C1334A5C");

        tree.Get(key.AsSpan()).Should().BeEquivalentTo(key);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertKey1Value1(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        byte[] key = VerkleTestUtils.Array1To32;

        tree.Insert((Hash256)key, key);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6F5E7CFC3A158A64E5718B0D2F18F564171342380F5808F3D2A82F7E7F3C2778");

        tree.Get(key.AsSpan()).Should().BeEquivalentTo(key);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertSameStemTwoLeaves(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        byte[] keyA = VerkleTestUtils.Array1To32;

        byte[] keyB = VerkleTestUtils.Array1To32Last128;

        tree.Insert((Hash256)keyA, keyA);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6F5E7CFC3A158A64E5718B0D2F18F564171342380F5808F3D2A82F7E7F3C2778");
        tree.Insert((Hash256)keyB, keyB);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "14EE5E5C5B698E363055B41DD3334F8168C7FCA4F85C5E30AB39CF9CC2FEEF70");

        tree.Get(keyA.AsSpan()).Should().BeEquivalentTo(keyA);
        tree.Get(keyB.AsSpan()).Should().BeEquivalentTo(keyB);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertKey1Val1Key2Val2(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        byte[] keyA = VerkleTestUtils.EmptyArray;
        byte[] keyB = VerkleTestUtils.ArrayAll1;

        tree.Insert((Hash256)keyA, keyA);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6B630905CE275E39F223E175242DF2C1E8395E6F46EC71DCE5557012C1334A5C");
        tree.Insert((Hash256)keyB, keyB);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "5E208CDBA664A7B8FBDC26A1C1185F153A5F721CBA389625C18157CEF7D4931C");

        tree.Get(keyA.AsSpan()).Should().BeEquivalentTo(keyA);
        tree.Get(keyB.AsSpan()).Should().BeEquivalentTo(keyB);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertLongestPath(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        byte[] keyA = VerkleTestUtils.EmptyArray;
        byte[] keyB = (byte[])VerkleTestUtils.EmptyArray.Clone();
        keyB[30] = 1;

        tree.Insert((Hash256)keyA, keyA);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6B630905CE275E39F223E175242DF2C1E8395E6F46EC71DCE5557012C1334A5C");
        tree.Insert((Hash256)keyB, keyB);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "3258D722AEA34B5AE7CB24A9B0175EDF0533C651FA09592E823B5969C729FB88");

        tree.Get(keyA.AsSpan()).Should().BeEquivalentTo(keyA);
        tree.Get(keyB.AsSpan()).Should().BeEquivalentTo(keyB);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertAndTraverseLongestPath(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        byte[] keyA = VerkleTestUtils.EmptyArray;
        tree.Insert((Hash256)keyA, keyA);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6B630905CE275E39F223E175242DF2C1E8395E6F46EC71DCE5557012C1334A5C");

        byte[] keyB = (byte[])VerkleTestUtils.EmptyArray.Clone();
        keyB[30] = 1;
        tree.Insert((Hash256)keyB, keyB);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "3258D722AEA34B5AE7CB24A9B0175EDF0533C651FA09592E823B5969C729FB88");

        byte[] keyC = (byte[])VerkleTestUtils.EmptyArray.Clone();
        keyC[29] = 1;
        tree.Insert((Hash256)keyC, keyC);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "5B82B26A1A7E00A1E997ABD51FE3075D05F54AA4CB1B3A70607E62064FADAA82");

        tree.Get(keyA.AsSpan()).Should().BeEquivalentTo(keyA);
        tree.Get(keyB.AsSpan()).Should().BeEquivalentTo(keyB);
        tree.Get(keyC.AsSpan()).Should().BeEquivalentTo(keyC);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestEmptyTrie(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        tree.Commit();
        tree.StateRoot.Bytes.ToArray().Should().BeEquivalentTo(FrE.Zero.ToBytes().ToArray());
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestSimpleUpdate(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        byte[] key = VerkleTestUtils.Array1To32;
        byte[] value = VerkleTestUtils.EmptyArray;
        tree.Insert((Hash256)key, value);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "140A25B322EAA1ADACD0EE1BB135ECA7B78FCF02B4B19E4A55B26B7A434F42AC");
        tree.Get(key.AsSpan()).Should().BeEquivalentTo(value);

        tree.Insert((Hash256)key, key);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6F5E7CFC3A158A64E5718B0D2F18F564171342380F5808F3D2A82F7E7F3C2778");
        tree.Get(key.AsSpan()).Should().BeEquivalentTo(key);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGet(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);

        tree.Insert(VerkleTestUtils.KeyVersion, VerkleTestUtils.EmptyArray);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "476C50753A22B270DA9D409C0F9AB655AB2506CE4EF831481DD455F0EA730FEF");

        tree.Insert(VerkleTestUtils.KeyBalance, VerkleTestUtils.ArrayAll0Last2);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "6D9E4F2D418DE2822CE9C2F4193C0E155E4CAC6CF4170E44098DC49D4B571B7B");

        tree.Insert(VerkleTestUtils.KeyNonce, VerkleTestUtils.EmptyArray);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "5C7AE53FE2AAE9852127140C1E2F5122BB3759A7975C0E7A1AEC7CAF7C711FDE");

        tree.Insert(VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "3FD5FA25042DB0304792BFC007514DA5B777516FFBDAA5658AF36D355ABD9BD8");

        tree.Insert(VerkleTestUtils.KeyCodeSize, VerkleTestUtils.EmptyArray);
        tree.Commit();
        AssertRootHash(tree.StateRoot.Bytes,
            "006BD679A8204502DCBF9A002F0B828AECF5A29A3A5CE454E651E3A96CC02FE2");

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestValueSameBeforeAndAfterFlush(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);


        tree.Insert(VerkleTestUtils.KeyVersion, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyBalance, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyNonce, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils.KeyCodeSize, VerkleTestUtils.EmptyArray);

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);

        tree.Commit();
        tree.CommitTree(0);

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGetMultiBlock(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);

        tree.Insert(VerkleTestUtils.KeyVersion, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyBalance, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyNonce, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils.KeyCodeSize, VerkleTestUtils.EmptyArray);

        tree.Commit();
        tree.CommitTree(0);

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);

        tree.Insert(VerkleTestUtils.KeyVersion, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert(VerkleTestUtils.KeyBalance, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert(VerkleTestUtils.KeyNonce, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert(VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils.KeyCodeSize, VerkleTestUtils.ArrayAll0Last2);

        tree.Commit();
        tree.CommitTree(1);

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
    }


    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertConvertStemToBranch(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        Random rand = new Random(0);
        Span<byte> buffer = stackalloc byte[32];

        rand.NextBytes(buffer);
        tree.Insert(new Hash256("0x7252c3792d6c7ea1132c151cddc658fb422c78c380dcf6cef2015f699c810100"), buffer);
        tree.Commit();

        rand.NextBytes(buffer);
        tree.Insert(new Hash256("0x7252c3792d6c7ea1132c151cddc658fb422c78c380dcf6cef2015f699c810100"), buffer);

        rand.NextBytes(buffer);
        tree.Insert(new Hash256("0x723cd68b63171b41a10b384f8d625db58df3a26e5ec81471efa587e213e70f00"), buffer);
        tree.Commit();

        tree.StateRoot.Bytes.ToHexString().Should().Be("1fdf11b3a95ee92db8eefa795dd24c4678693392bdcf4722172c59c41d381c1b");
    }

    [TestCase(DbMode.MemDb)]
    public void TestBeverlyHillGenesis(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
        byte[][] keys =
        [
            [
                80,
                91,
                197,
                250,
                186,
                158,
                22,
                244,
                39,
                111,
                133,
                220,
                198,
                184,
                196,
                37,
                196,
                170,
                83,
                13,
                248,
                137,
                214,
                145,
                207,
                141,
                22,
                250,
                127,
                178,
                242,
                98
            ],
            [
                80,
                91,
                197,
                250,
                186,
                158,
                22,
                244,
                39,
                111,
                133,
                220,
                198,
                184,
                196,
                37,
                196,
                170,
                83,
                13,
                248,
                137,
                214,
                145,
                207,
                141,
                22,
                250,
                127,
                178,
                242,
                0
            ],
            [
                80,
                91,
                197,
                250,
                186,
                158,
                22,
                244,
                39,
                111,
                133,
                220,
                198,
                184,
                196,
                37,
                196,
                170,
                83,
                13,
                248,
                137,
                214,
                145,
                207,
                141,
                22,
                250,
                127,
                178,
                242,
                1
            ],
            [
                80,
                91,
                197,
                250,
                186,
                158,
                22,
                244,
                39,
                111,
                133,
                220,
                198,
                184,
                196,
                37,
                196,
                170,
                83,
                13,
                248,
                137,
                214,
                145,
                207,
                141,
                22,
                250,
                127,
                178,
                242,
                2
            ],
            [
                80,
                91,
                197,
                250,
                186,
                158,
                22,
                244,
                39,
                111,
                133,
                220,
                198,
                184,
                196,
                37,
                196,
                170,
                83,
                13,
                248,
                137,
                214,
                145,
                207,
                141,
                22,
                250,
                127,
                178,
                242,
                3
            ],
            [
                80,
                28,
                126,
                51,
                3,
                54,
                20,
                30,
                142,
                44,
                127,
                93,
                139,
                146,
                112,
                200,
                182,
                35,
                165,
                99,
                140,
                74,
                215,
                203,
                100,
                29,
                142,
                136,
                89,
                75,
                19,
                0
            ],
            [
                80,
                28,
                126,
                51,
                3,
                54,
                20,
                30,
                142,
                44,
                127,
                93,
                139,
                146,
                112,
                200,
                182,
                35,
                165,
                99,
                140,
                74,
                215,
                203,
                100,
                29,
                142,
                136,
                89,
                75,
                19,
                1
            ],
            [
                80,
                28,
                126,
                51,
                3,
                54,
                20,
                30,
                142,
                44,
                127,
                93,
                139,
                146,
                112,
                200,
                182,
                35,
                165,
                99,
                140,
                74,
                215,
                203,
                100,
                29,
                142,
                136,
                89,
                75,
                19,
                2
            ],
            [
                80,
                28,
                126,
                51,
                3,
                54,
                20,
                30,
                142,
                44,
                127,
                93,
                139,
                146,
                112,
                200,
                182,
                35,
                165,
                99,
                140,
                74,
                215,
                203,
                100,
                29,
                142,
                136,
                89,
                75,
                19,
                3
            ]

        ];
        byte[][] values =
        [
            [
                245,
                165,
                253,
                66,
                209,
                106,
                32,
                48,
                39,
                152,
                239,
                110,
                211,
                9,
                151,
                155,
                67,
                0,
                61,
                35,
                32,
                217,
                240,
                232,
                234,
                152,
                49,
                169,
                39,
                89,
                251,
                75
            ],
            [
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0
            ],
            [
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0
            ],
            [
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0
            ],
            [
                197,
                210,
                70,
                1,
                134,
                247,
                35,
                60,
                146,
                126,
                125,
                178,
                220,
                199,
                3,
                192,
                229,
                0,
                182,
                83,
                202,
                130,
                39,
                59,
                123,
                250,
                216,
                4,
                93,
                133,
                164,
                112
            ],
            [
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0
            ],
            [
                1,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0
            ],
            [
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0
            ],
            [
                197,
                210,
                70,
                1,
                134,
                247,
                35,
                60,
                146,
                126,
                125,
                178,
                220,
                199,
                3,
                192,
                229,
                0,
                182,
                83,
                202,
                130,
                39,
                59,
                123,
                250,
                216,
                4,
                93,
                133,
                164,
                112
            ]

        ];
        byte[][] expectedRootHash =
        [
            [
                65,
                218,
                34,
                122,
                247,
                60,
                5,
                240,
                251,
                52,
                246,
                140,
                37,
                216,
                19,
                241,
                119,
                101,
                100,
                139,
                61,
                137,
                58,
                14,
                46,
                134,
                177,
                141,
                32,
                154,
                1,
                59
            ],
            [
                69,
                56,
                80,
                6,
                24,
                134,
                42,
                30,
                244,
                7,
                143,
                117,
                127,
                102,
                1,
                223,
                77,
                33,
                172,
                250,
                47,
                138,
                224,
                44,
                49,
                218,
                223,
                138,
                225,
                174,
                75,
                206
            ],
            [
                1,
                193,
                213,
                250,
                133,
                201,
                192,
                237,
                111,
                140,
                199,
                105,
                38,
                248,
                215,
                48,
                91,
                252,
                209,
                7,
                111,
                242,
                47,
                75,
                91,
                142,
                31,
                125,
                12,
                184,
                18,
                245
            ],
            [
                81,
                28,
                164,
                25,
                6,
                44,
                54,
                172,
                110,
                118,
                89,
                173,
                112,
                198,
                226,
                209,
                24,
                221,
                92,
                3,
                221,
                62,
                85,
                11,
                85,
                179,
                58,
                178,
                78,
                187,
                238,
                71
            ],
            [
                57,
                217,
                114,
                85,
                250,
                173,
                204,
                43,
                147,
                248,
                170,
                130,
                74,
                146,
                15,
                162,
                181,
                125,
                23,
                235,
                3,
                110,
                20,
                156,
                89,
                185,
                169,
                139,
                69,
                112,
                37,
                175
            ],
            [
                3,
                81,
                31,
                1,
                31,
                195,
                243,
                62,
                60,
                100,
                13,
                77,
                215,
                21,
                9,
                92,
                237,
                104,
                172,
                25,
                234,
                240,
                207,
                142,
                94,
                213,
                237,
                176,
                195,
                163,
                39,
                87
            ],
            [
                83,
                211,
                141,
                144,
                197,
                72,
                206,
                46,
                100,
                220,
                68,
                98,
                90,
                239,
                120,
                251,
                113,
                102,
                18,
                170,
                98,
                238,
                138,
                174,
                249,
                120,
                142,
                238,
                79,
                192,
                75,
                59
            ],
            [
                91,
                25,
                255,
                166,
                55,
                136,
                143,
                224,
                38,
                101,
                85,
                250,
                65,
                108,
                59,
                161,
                105,
                247,
                140,
                181,
                185,
                207,
                120,
                76,
                92,
                218,
                72,
                103,
                18,
                0,
                214,
                144
            ],
            [
                5,
                153,
                156,
                66,
                226,
                161,
                228,
                136,
                209,
                248,
                42,
                235,
                222,
                221,
                118,
                125,
                87,
                137,
                13,
                106,
                11,
                70,
                26,
                248,
                3,
                21,
                218,
                184,
                201,
                164,
                194,
                198
            ]

        ];


        for (int i = 0; i < keys.Length; i++)
        {
            tree.Insert((Hash256)keys[i], values[i]);
            tree.Commit();
            AssertRootHash(tree.StateRoot.Bytes.ToArray().AsEnumerable(), expectedRootHash[i].AsEnumerable());
        }
    }

    private static void AssertRootHash(Span<byte> realRootHash, string expectedRootHash)
    {
        Convert.ToHexString(realRootHash).Should()
            .BeEquivalentTo(expectedRootHash);
    }

    private static void AssertRootHash(IEnumerable<byte> realRootHash, IEnumerable<byte> expectedRootHash)
    {
        realRootHash.Should().BeEquivalentTo(expectedRootHash);
    }
}
