using System.Text;
using NUnit.Framework;

namespace Lantern.Discv5.Rlp.Tests;

[TestFixture]
public class RlpEncoderTests
{
    [Test]
    public void Test_RlpEncoder_ShouldEncodeEmptyStringsCorrectly()
    {
        var emptyString = "";
        var rlpEncoded = RlpEncoder.EncodeString(emptyString, Encoding.UTF8);
        var rlpExpected = new byte[] { 128 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeStringCorrectly()
    {
        var rlpEncoded = RlpEncoder.EncodeString("cat", Encoding.UTF8);
        var rlpExpected = new byte[] { 131, 99, 97, 116 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeStringsCorrectly()
    {
        var strings = new[] { "cat", "dog" };
        var rlpEncoded = RlpEncoder.EncodeStringCollection(strings, Encoding.UTF8);
        var rlpExpected = new byte[] { 200, 131, 99, 97, 116, 131, 100, 111, 103 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeZeroHexCorrectly()
    {
        var rlpEncoded = RlpEncoder.EncodeHexString("00");
        var rlpExpected = new byte[] { 0 };
        Console.WriteLine(string.Join(", ", rlpEncoded));
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeZeroIntegerCorrectly()
    {
        var rlpEncoded = RlpEncoder.EncodeInteger(0);
        var rlpExpected = new byte[] { 128 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeSmallIntegerCorrectly()
    {
        var rlpEncoded = RlpEncoder.EncodeInteger(23);
        var rlpExpected = new byte[] { 23 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeLargeIntegerCorrectly()
    {
        var rlpEncoded = RlpEncoder.EncodeInteger(1024);
        var rlpExpected = new byte[] { 130, 4, 0 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeBytesCorrectly()
    {
        var bytes = new byte[] { 242, 237, 147, 36, 77, 171, 90, 246, 161, 246, 113, 170 };
        var rlpEncoded = RlpEncoder.EncodeBytes(bytes);
        var rlpExpected = new byte[] { 140, 242, 237, 147, 36, 77, 171, 90, 246, 161, 246, 113, 170 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeCollectionOfBytesCorrectly()
    {
        var bytes = Array.Empty<byte>();
        var rlpEncoded = RlpEncoder.EncodeCollectionOfBytes(bytes);
        var rlpExpected = new byte[] { 192 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeCollectionsOfBytesCorrectly()
    {
        var bytes = Array.Empty<byte>();
        var rlpEncoded = RlpEncoder.EncodeCollectionsOfBytes(bytes);
        var rlpExpected = new byte[] { 193, 192 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeEncodeCollectionsOfBytesCorrectly()
    {
        var firstBytes = new byte[] { 3, 8, 21 };
        var secondBytes = new byte[] { 23 };

        var rlpEncoded = RlpEncoder.EncodeCollectionsOfBytes(firstBytes, secondBytes);
        var rlpExpected = new byte[] { 197, 195, 3, 8, 21, 23 };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeEachByteInBytesCorrectly()
    {
        var bytes = new byte[]
        {
            242, 237, 147, 36, 77, 171, 90, 246, 161, 246, 113, 170, 45, 0, 132, 26, 136, 115, 121, 110, 99, 110, 101,
            116, 115, 0, 131, 116, 99, 112, 130, 35, 40, 131, 117, 100, 112, 130, 35, 40
        };
        var rlpEncoded = RlpEncoder.EncodeByteItemsAsCollection(bytes);
        var rlpExpected = new byte[]
        {
            246, 129, 242, 129, 237, 129, 147, 36, 77, 129, 171, 90, 129, 246, 129, 161, 129, 246, 113, 129, 170, 45, 0, 129, 132, 26, 129, 136, 115, 121, 110, 99, 110, 101, 116, 115, 0, 129, 131, 116, 99, 112, 129, 130, 35, 40, 129, 131, 117, 100, 112, 129, 130, 35, 40
        };
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeCollectionOfThresholdSize_Correctly()
    {
        var bytes = Enumerable.Range(0, 55).Select(_ => (byte)255).ToArray();
        var rlpEncoded = RlpEncoder.EncodeCollectionOfBytes(bytes);
        var rlpExpected = new byte[] { 192 + 55 }.Concat(bytes).ToArray();
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }

    [Test]
    public void Test_RlpEncoder_ShouldEncodeCollectionOfMoreThanThresholdSize_Correctly()
    {
        var bytes = Enumerable.Range(0, 56).Select(_ => (byte)255).ToArray();
        var rlpEncoded = RlpEncoder.EncodeCollectionOfBytes(bytes);
        var rlpExpected = new byte[] { 247 + 1, 56 }.Concat(bytes).ToArray();
        Assert.IsTrue(rlpEncoded.SequenceEqual(rlpExpected));
    }
}
