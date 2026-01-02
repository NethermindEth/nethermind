using System.Text;
using NUnit.Framework;

namespace Lantern.Discv5.Rlp.Tests;

[TestFixture]
public class RlpDecoderTests
{
    [Test]
    public void Test_RlpDecoder_ShouldDecodeSingleCharacterCorrectly()
    {
        var rawValue = "a";
        var value = Encoding.UTF8.GetBytes(rawValue);
        var encodedBytes = RlpEncoder.EncodeBytes(value);
        var decodedBytes = RlpDecoder.Decode(encodedBytes);
        Assert.AreEqual(decodedBytes.Length, 1);
        Assert.AreEqual(rawValue, Encoding.UTF8.GetString(decodedBytes[0]));
    }

    [Test]
    public void Test_RlpDecoder_ShouldDecodeShortStringCorrectly()
    {
        var rawValue = "abc";
        var value = Encoding.UTF8.GetBytes(rawValue);
        var encodedBytes = RlpEncoder.EncodeBytes(value);
        var decodedBytes = RlpDecoder.Decode(encodedBytes);
        Assert.AreEqual(decodedBytes.Length, 1);
        Assert.AreEqual(rawValue, Encoding.UTF8.GetString(decodedBytes[0]));
    }

    [Test]
    public void Test_RlpDecoder_ShouldDecodeLongStringCorrectly()
    {
        var rawValue = "frefefefrfrsefsfsfsefesfsfsffrefefefrfrsefsfsfsefesfsfsf";
        var value = Encoding.UTF8.GetBytes(rawValue);
        var encodedBytes = RlpEncoder.EncodeBytes(value);
        var decodedBytes = RlpDecoder.Decode(encodedBytes);
        Assert.AreEqual(decodedBytes.Length, 1);
        Assert.AreEqual(rawValue, Encoding.UTF8.GetString(decodedBytes[0]));
    }

    [Test]
    public void Test_RlpDecoder_ShouldDecodeSmallIntegerCorrectly()
    {
        var rawValue = 23;
        var value = ByteArrayUtils.ToBigEndianBytesTrimmed(rawValue);
        var encodedBytes = RlpEncoder.EncodeBytes(value);
        var decodedBytes = RlpDecoder.Decode(encodedBytes);
        Assert.AreEqual(decodedBytes.Length, 1);
        Assert.AreEqual(rawValue, RlpExtensions.ByteArrayToUInt64(decodedBytes[0]));
    }

    [Test]
    public void Test_RlpDecoder_ShouldDecodeLargeIntegerCorrectly()
    {
        var rawValue = 999999999;
        var value = ByteArrayUtils.ToBigEndianBytesTrimmed(rawValue);
        var encodedBytes = RlpEncoder.EncodeBytes(value);
        var decodedBytes = RlpDecoder.Decode(encodedBytes);
        Assert.AreEqual(decodedBytes.Length, 1);
        Assert.AreEqual(rawValue, RlpExtensions.ByteArrayToUInt64(decodedBytes[0]));
    }

    [Test]
    public void Test_RlpDecoder_ShouldDecodeShortCollectionCorrectly()
    {
        var rawValue = new byte[] { 4, 23, 45, 6 };
        var bytes = RlpEncoder.EncodeCollectionOfBytes(rawValue);
        var encodedBytes = RlpDecoder.Decode(bytes);
        Assert.IsTrue(rawValue.SequenceEqual((byte[])encodedBytes[0]));
    }

    [Test]
    public void Test_RlpDecoder_ShouldDecodeShortCollectionsCorrectly()
    {
        var rawValue = new byte[] { 4, 23, 45, 6 };
        var bytes = RlpEncoder.EncodeCollectionsOfBytes(rawValue);
        var listsRlp = RlpDecoder.Decode(bytes);
        Assert.AreEqual(listsRlp.Length, 1);
        var firstRlp = RlpDecoder.Decode(listsRlp[0]);

        Assert.IsTrue(rawValue.SequenceEqual((byte[])firstRlp[0]));
    }

    [Test]
    public void Test_RlpDecoder_ShouldDecodeFindNodes()
    {
        var rawValue = "f8b188b47225c9674f99d306f8a5f8a3b840df79b4ffe6fe452449180234ff8e1509237b6decc6d054dc34cbba5f7dcf036a3f067a213dc9944aa5c761ffe450fc9ad032a050a2fb4467198bc3f0d6ac0eb68601926acf868083657468c7c6849b192ad080826964827634826970843b1fcea189736563703235366b31a102ee59efb761353ad2e7458f2e127cb110a5571beeff0d25e95de840331e5725ce84736e6170c08374637082765f8375647082765f";
        var value = Convert.FromHexString(rawValue);
        var findNodesResponseRlp = RlpDecoder.Decode(value);
        Assert.AreEqual(1, findNodesResponseRlp.Length);

        var findNodesResponseItems = findNodesResponseRlp.ToArray().Select(b => RlpDecoder.Decode(b).ToArray()).ToArray();
        Assert.AreEqual(3, findNodesResponseItems[0].Length);

        var firstItemEnrsArrayRlp = RlpDecoder.Decode(findNodesResponseItems[0][2]);

        var enrsItems = firstItemEnrsArrayRlp.ToArray().Select(b => RlpDecoder.Decode(b).ToArray()).ToArray();
        Assert.AreEqual(1, enrsItems.Length);
        Assert.AreEqual(16, enrsItems[0].Length);
    }

    [Test]
    public void Test_RlpDecoder_ShouldDecodeFindNodes_WithMultipleEnrs()
    {
        var rawValue = "f9015788b47225c9674f99d306f9014af8a3b840df79b4ffe6fe452449180234ff8e1509237b6decc6d054dc34cbba5f7dcf036a3f067a213dc9944aa5c761ffe450fc9ad032a050a2fb4467198bc3f0d6ac0eb68601926acf868083657468c7c6849b192ad080826964827634826970843b1fcea189736563703235366b31a102ee59efb761353ad2e7458f2e127cb110a5571beeff0d25e95de840331e5725ce84736e6170c08374637082765f8375647082765ff8a3b840df79b4ffe6fe452449180234ff8e1509237b6decc6d054dc34cbba5f7dcf036a3f067a213dc9944aa5c761ffe450fc9ad032a050a2fb4467198bc3f0d6ac0eb68601926acf868083657468c7c6849b192ad080826964827634826970843b1fcea189736563703235366b31a102ee59efb761353ad2e7458f2e127cb110a5571beeff0d25e95de840331e5725ce84736e6170c08374637082765f8375647082765f";
        var value = Convert.FromHexString(rawValue);
        var findNodesResponseRlp = RlpDecoder.Decode(value);
        Assert.AreEqual(1, findNodesResponseRlp.Length);

        var findNodesResponseItems = findNodesResponseRlp.ToArray().Select(b => RlpDecoder.Decode(b).ToArray()).ToArray();
        Assert.AreEqual(3, findNodesResponseItems[0].Length);

        var firstItemEnrsArrayRlp = RlpDecoder.Decode(findNodesResponseItems[0][2]);

        var enrsItems = firstItemEnrsArrayRlp.ToArray().Select(b => RlpDecoder.Decode(b).ToArray()).ToArray();
        Assert.AreEqual(2, enrsItems.Length);
        Assert.AreEqual(16, enrsItems[0].Length);
        Assert.AreEqual(16, enrsItems[1].Length);
    }
}
