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
        var data = Bytes.FromHexString("ff0d54412868ab2569622781556c0b41264d9dae313826adad7b60da4b441e67");

        var index = new UInt256(data, true);
        Hash256? storageKey = AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, index);
        storageKey.BytesToArray().Should()
            .BeEquivalentTo(Bytes.FromHexString("0x0beaa63f5273c76e7b673048a478dd85e970f5657bc02dead742b9246e101e67"));

    }

    [Test]
    public void TestGetTreeKey()
    {
        Span<byte> addr = new byte[32];
        for (var i = 0; i < 16; i++)
        {
            addr[1 + 2 * i] = 255;
        }

        UInt256 n = 1;
        n <<= 129;
        n += 3;
        var key = PedersenHash.Hash(addr, n);
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
        value[..6].Should().BeEquivalentTo(new byte[] { 0, 97, 1, 2, 3, 4 });
        value[6..32].Should().BeEquivalentTo(new byte[26]);

        var code2 =
            Bytes.FromHexString(
                "0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c641e1f202122232425262728292a2b2c2d");
        var firstCodeChunk = Bytes.FromHexString("0x00000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c641e");
        var secondCodeChunk = Bytes.FromHexString("0x041f202122232425262728292a2b2c2d");

        codeEnumerator = new CodeChunkEnumerator(code2);

        codeEnumerator.TryGetNextChunk(out value);
        value.Should().NotBeNull();
        value.Should().BeEquivalentTo(firstCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value.Should().NotBeNull();
        value[..16].Should().BeEquivalentTo(secondCodeChunk);
        value[16..32].Should().BeEquivalentTo(new byte[16]);

    }

    [Test]
    public void SetCodeEdgeCases1()
    {
        byte[] code = Bytes.FromHexString("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d7f1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f4041");
        byte[] firstCodeChunk = Bytes.FromHexString("0x00000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d7f");
        byte[] secondCodeChunk = Bytes.FromHexString("0x1f1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d");
        byte[] thirdCodeChunk = Bytes.FromHexString("0x013e3f4041");

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
            Bytes.FromHexString(
                "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d7e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f4041");
        byte[] firstCodeChunk = Bytes.FromHexString("00000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d7e");
        byte[] secondCodeChunk =
            Bytes.FromHexString("1f1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d");
        byte[] thirdCodeChunk = Bytes.FromHexString("003e3f4041");

        Console.WriteLine(code.ToHexString());
        Console.WriteLine(firstCodeChunk.ToHexString());
        Console.WriteLine(secondCodeChunk.ToHexString());
        Console.WriteLine(thirdCodeChunk.ToHexString());

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
        byte[] code = Bytes.FromHexString("0x5f0160030461060708080a0b0c0d636465666768696a6b6c6d6e707172737475767778797a7b7c7d7e7f2a2b2c2d2e2f303132333435363738393a3b3c3d3e3f4041");
        byte[] firstCodeChunk = Bytes.FromHexString("0x005f0160030461060708080a0b0c0d636465666768696a6b6c6d6e7071727374");
        byte[] secondCodeChunk = Bytes.FromHexString("0x1375767778797a7b7c7d7e7f2a2b2c2d2e2f303132333435363738393a3b3c3d");
        byte[] thirdCodeChunk = Bytes.FromHexString("0x003e3f4041");

        var codeEnumerator = new CodeChunkEnumerator(code);

        codeEnumerator.TryGetNextChunk(out byte[] value);
        value.Should().BeEquivalentTo(firstCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value.Should().BeEquivalentTo(secondCodeChunk);

        codeEnumerator.TryGetNextChunk(out value);
        value[..5].Should().BeEquivalentTo(thirdCodeChunk);
    }
}
