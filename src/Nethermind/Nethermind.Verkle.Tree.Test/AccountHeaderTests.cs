using System;
using System.Numerics;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Int256;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree.Test;

[TestFixture]
public class AccountHeaderTests
{

    [Test]
    public void TestGetStorageKeyWithoutOverflow()
    {
        var address = new Address(Bytes.FromHexString("0xc2494a4e8eebd8e63bd3a5c91b60206661d396d5"));
        byte[] data = Bytes.FromHexString("ff0d54412868ab2569622781556c0b41264d9dae313826adad7b60da4b441e67");

        var index = new UInt256(data, true);
        Hash256? storageKey = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, index);
        storageKey.BytesToArray().Should()
            .BeEquivalentTo(Bytes.FromHexString("0x0beaa63f5273c76e7b673048a478dd85e970f5657bc02dead742b9246e101e67"));

    }

    [Test]
    public void TestGetTreeKey()
    {
        Span<byte> addr = new byte[32];
        for (int i = 0; i < 16; i++)
        {
            addr[1 + 2 * i] = 255;
        }

        UInt256 n = 1;
        n = n << 129;
        n = n + 3;
        byte[] key = PedersenHash.Hash(addr, n);
        key[31] = 1;

        key.ToHexString().Should().BeEquivalentTo("6ede905763d5856cd2d67936541e82aa78f7141bf8cd5ff6c962170f3e9dc201");
    }

    [Test]
    public void SetAccountWithCode()
    {
        byte[] code = [1, 2, 3, 4];
        var codeEnumerator = new CodeChunkEnumerator(code);

        codeEnumerator.TryGetNextChunk(out byte[] value);
        value.Should().NotBeNull();
        value[..5].Should().BeEquivalentTo(new byte[] { 0, 1, 2, 3, 4 });
        value[5..32].Should().BeEquivalentTo(new byte[27]);

        codeEnumerator.TryGetNextChunk(out value).Should().BeFalse();
    }

    [Test]
    public void SetAccountWithCodePushOpcodes()
    {
        byte[] code1 = [97, 1, 2, 3, 4];
        CodeChunkEnumerator codeEnumerator = new CodeChunkEnumerator(code1);

        codeEnumerator.TryGetNextChunk(out byte[] value);
        value.Should().NotBeNull();
        value[0..6].Should().BeEquivalentTo(new byte[] { 0, 97, 1, 2, 3, 4 });
        value[6..32].Should().BeEquivalentTo(new byte[26]);

        byte[] code2 =
        [
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10,
            11,
            12,
            13,
            14,
            15,
            16,
            17,
            18,
            19,
            20,
            21,
            22,
            23,
            24,
            25,
            26,
            27,
            28,
            100,
            30,
            31,
            32,
            33,
            34,
            35,
            36,
            37,
            38,
            39,
            40,
            41,
            42,
            43,
            44,
            45
        ];
        byte[] firstCodeChunk =
        [
            0,
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10,
            11,
            12,
            13,
            14,
            15,
            16,
            17,
            18,
            19,
            20,
            21,
            22,
            23,
            24,
            25,
            26,
            27,
            28,
            100,
            30
        ];
        byte[] secondCodeChunk =
        [
            4,
            31,
            32,
            33,
            34,
            35,
            36,
            37,
            38,
            39,
            40,
            41,
            42,
            43,
            44,
            45
        ];

        codeEnumerator = new CodeChunkEnumerator(code2);

        codeEnumerator.TryGetNextChunk(out value);
        value.Should().NotBeNull();
        value.Should().BeEquivalentTo(firstCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value.Should().NotBeNull();
        value[0..16].Should().BeEquivalentTo(secondCodeChunk);
        value[16..32].Should().BeEquivalentTo(new byte[16]);

    }

    [Test]
    public void SetCodeEdgeCases1()
    {
        byte[] code =
        [
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10,
            11,
            12,
            13,
            14,
            15,
            16,
            17,
            18,
            19,
            20,
            21,
            22,
            23,
            24,
            25,
            26,
            27,
            28,
            29,
            127,
            31,
            32,
            33,
            34,
            35,
            36,
            37,
            38,
            39,
            40,
            41,
            42,
            43,
            44,
            45,
            46,
            47,
            48,
            49,
            50,
            51,
            52,
            53,
            54,
            55,
            56,
            57,
            58,
            59,
            60,
            61,
            62,
            63,
            64,
            65
        ];
        byte[] firstCodeChunk =
        [
            0,
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10,
            11,
            12,
            13,
            14,
            15,
            16,
            17,
            18,
            19,
            20,
            21,
            22,
            23,
            24,
            25,
            26,
            27,
            28,
            29,
            127
        ];
        byte[] secondCodeChunk =
        [
            31,
            31,
            32,
            33,
            34,
            35,
            36,
            37,
            38,
            39,
            40,
            41,
            42,
            43,
            44,
            45,
            46,
            47,
            48,
            49,
            50,
            51,
            52,
            53,
            54,
            55,
            56,
            57,
            58,
            59,
            60,
            61
        ];
        byte[] thirdCodeChunk =
        [
            1,
            62,
            63,
            64,
            65
        ];
        var codeEnumerator = new CodeChunkEnumerator(code);

        codeEnumerator.TryGetNextChunk(out byte[] value);
        value.Should().BeEquivalentTo(firstCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value.Should().BeEquivalentTo(secondCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value[..5].Should().BeEquivalentTo(thirdCodeChunk);
    }

    [Test]
    public void SetCodeEdgeCases2()
    {
        byte[] code =
        [
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10,
            11,
            12,
            13,
            14,
            15,
            16,
            17,
            18,
            19,
            20,
            21,
            22,
            23,
            24,
            25,
            26,
            27,
            28,
            29,
            126,
            31,
            32,
            33,
            34,
            35,
            36,
            37,
            38,
            39,
            40,
            41,
            42,
            43,
            44,
            45,
            46,
            47,
            48,
            49,
            50,
            51,
            52,
            53,
            54,
            55,
            56,
            57,
            58,
            59,
            60,
            61,
            62,
            63,
            64,
            65
        ];
        byte[] firstCodeChunk =
        [
            0,
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
            10,
            11,
            12,
            13,
            14,
            15,
            16,
            17,
            18,
            19,
            20,
            21,
            22,
            23,
            24,
            25,
            26,
            27,
            28,
            29,
            126
        ];
        byte[] secondCodeChunk =
        [
            31,
            31,
            32,
            33,
            34,
            35,
            36,
            37,
            38,
            39,
            40,
            41,
            42,
            43,
            44,
            45,
            46,
            47,
            48,
            49,
            50,
            51,
            52,
            53,
            54,
            55,
            56,
            57,
            58,
            59,
            60,
            61
        ];
        byte[] thirdCodeChunk =
        [
            0,
            62,
            63,
            64,
            65
        ];

        var codeEnumerator = new CodeChunkEnumerator(code);

        codeEnumerator.TryGetNextChunk(out byte[] value);
        value.Should().BeEquivalentTo(firstCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value.Should().BeEquivalentTo(secondCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value[..5].Should().BeEquivalentTo(thirdCodeChunk);

    }

    [Test]
    public void SetCodeEdgeCases3()
    {
        byte[] code =
        [
            95,
            1,
            96,
            3,
            4,
            97,
            6,
            7,
            8,
            98,
            10,
            11,
            12,
            13,
            99,
            100,
            101,
            102,
            103,
            104,
            105,
            106,
            107,
            108,
            109,
            110,
            112,
            113,
            114,
            115,
            116,
            117,
            118,
            119,
            120,
            121,
            122,
            123,
            124,
            125,
            126,
            127,
            42,
            43,
            44,
            45,
            46,
            47,
            48,
            49,
            50,
            51,
            52,
            53,
            54,
            55,
            56,
            57,
            58,
            59,
            60,
            61,
            62,
            63,
            64,
            65
        ];
        byte[] firstCodeChunk =
        [
            0,
            95,
            1,
            96,
            3,
            4,
            97,
            6,
            7,
            8,
            98,
            10,
            11,
            12,
            13,
            99,
            100,
            101,
            102,
            103,
            104,
            105,
            106,
            107,
            108,
            109,
            110,
            112,
            113,
            114,
            115,
            116
        ];
        byte[] secondCodeChunk =
        [
            19,
            117,
            118,
            119,
            120,
            121,
            122,
            123,
            124,
            125,
            126,
            127,
            42,
            43,
            44,
            45,
            46,
            47,
            48,
            49,
            50,
            51,
            52,
            53,
            54,
            55,
            56,
            57,
            58,
            59,
            60,
            61
        ];
        byte[] thirdCodeChunk =
        [
            0,
            62,
            63,
            64,
            65
        ];

        var codeEnumerator = new CodeChunkEnumerator(code);

        codeEnumerator.TryGetNextChunk(out byte[] value);
        value.Should().BeEquivalentTo(firstCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value.Should().BeEquivalentTo(secondCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value[0..5].Should().BeEquivalentTo(thirdCodeChunk);
    }
}
