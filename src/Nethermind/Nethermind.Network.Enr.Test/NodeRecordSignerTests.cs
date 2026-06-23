// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Enr.Test;

public class NodeRecordSignerTests
{
    private const string TestPrivateKey = "b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291";

    [Test(Description = "https://eips.ethereum.org/EIPS/eip-778")]
    public void Is_correct_on_eip_test_vector()
    {
        const string expectedEnrString = "enr:" +
                                         "-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOo" +
                                         "nrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPK" +
                                         "Y0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8";

        // go back from Base64Url - also added the '=' padding at the end of the previous string
        // just to check how RLP should look (for debugging the test)
        string expectedEnrStringNonRfcBase64 = expectedEnrString
            .Replace("enr:", string.Empty)
            .Replace('-', '+')
            .Replace('_', '/') + /*padding*/ "=";

        byte[] expected = Convert.FromBase64String(expectedEnrStringNonRfcBase64);
        string expectedHexString = expected.ToHexString();
        Console.WriteLine("expected: " + expectedHexString);

        Ecdsa ecdsa = new();
        PrivateKey privateKey = new(TestPrivateKey);
        NodeRecordSigner signer = new(ecdsa, privateKey);
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new IpEntry(
            new IPAddress(Bytes.FromHexString("7f000001"))));
        nodeRecord.SetEntry(new UdpEntry(
            BinaryPrimitives.ReadInt16BigEndian(Bytes.FromHexString("765f"))));
        nodeRecord.SetEntry(new SecP256k1Entry(
            // new CompressedPublicKey("03a448f24c6d18e575453db13171562b71999873db5b286df957af199ec94617f7")));
            new CompressedPublicKey("03ca634cae0d49acb401d8a4c6b6fe8c55b70d115bf400769cc1400f3258cd3138")));
        nodeRecord.EnrSequence = 1; // override

        signer.Sign(nodeRecord);
        string enrString = nodeRecord.EnrString;
        Assert.That(enrString, Is.EqualTo(expectedEnrString));
    }

    [Test(Description = "https://eips.ethereum.org/EIPS/eip-778")]
    public void Can_verify_signature()
    {
        const string expectedEnrString = "enr:" +
                                         "-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOo" +
                                         "nrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPK" +
                                         "Y0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8";

        // go back from Base64Url - also added the '=' padding at the end of the previous string
        // just to check how RLP should look (for debugging the test)
        string expectedEnrStringNonRfcBase64 = expectedEnrString
            .Replace("enr:", string.Empty)
            .Replace('-', '+')
            .Replace('_', '/') + /*padding*/ "=";
        byte[] expected = Convert.FromBase64String(expectedEnrStringNonRfcBase64);
        string expectedHexString = expected.ToHexString();
        Console.WriteLine("expected: " + expectedHexString);

        Ecdsa ecdsa = new();
        PrivateKey privateKey = new(TestPrivateKey);

        NodeRecordSigner signer = new(ecdsa, privateKey);
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new IpEntry(
            new IPAddress(Bytes.FromHexString("7f000001"))));
        nodeRecord.SetEntry(new UdpEntry(
            BinaryPrimitives.ReadInt16BigEndian(Bytes.FromHexString("765f"))));

        CompressedPublicKey compressedPublicKey =
            new("03ca634cae0d49acb401d8a4c6b6fe8c55b70d115bf400769cc1400f3258cd3138");
        nodeRecord.SetEntry(new SecP256k1Entry(
            // new CompressedPublicKey("03a448f24c6d18e575453db13171562b71999873db5b286df957af199ec94617f7")));
            compressedPublicKey));

        nodeRecord.EnrSequence = 1; // override

        signer.Sign(nodeRecord);
        Assert.That(signer.Verify(nodeRecord), Is.True);
    }

    [TestCaseSource(nameof(InvalidRecordRlpCases))]
    public void Throws_when_record_is_invalid(Func<RlpStream> createRecord, Type exceptionType)
    {
        NodeRecordSigner signer = new(new Ecdsa());
        Assert.That(() => signer.Deserialize(createRecord()), Throws.TypeOf(exceptionType));
    }

    [TestCase("f897b840421561b4ed5de28a7100e0a5005ecc0ba6ba6cc18528061e811704c8794fec965cba63831051d134bdc801c0c90d31a30d241074095311ffe6628d5545478b770a83657468c7c68496516d06808269648276348269708436ed0a0a89736563703235366b31a103f5c110132b0374805d4453f55577cc9c58bb1a08f822b9b3722132e3095f69728374637082765f8375647082765f")]
    [TestCase("f897b8406fb9316953b51793ee43316fe14f2d0ac0b356b86815175c6d231840bd6f24e504bfa6492ccc1f4b0853b02ae44fbee861f52044dd08e4a23edf6187ea5e46e71583657468c7c68420c327fc80826964827634826970847f00000189736563703235366b31a102ba4be3a4095b23fe90a850709394476bf23c9788ad124325a6163f342e05a7308374637082765f8375647082765f")]
    [TestCase("f89fb8401d2ab9d1937f7d3524feec8edb45e3abc4e4a01ca227615502bcad2cd68eaf804fc5865f6a5551bd5c39f56ee4d4c005c69be3efc44f2a9ff312d71de13a62de8207ab83657468c7c6843de1adaf808269648276348269708467e4b73289736563703235366b31a102bb8f962e961a1d82dac4bc32b71e491da35bcd69e18bec31aba9b9fadd0e1a1184736e6170c08374637082765f8375647082765f")]
    public void Can_deserialize_and_verify_real_world_cases(string testCase)
    {
        Ecdsa ecdsa = new();
        PrivateKey privateKey = new(TestPrivateKey);
        NodeRecordSigner signer = new(ecdsa, privateKey);
        RlpStream rlpStream = Bytes.FromHexString(testCase).AsRlpStream();
        NodeRecord nodeRecord = signer.Deserialize(rlpStream);
        string hex = nodeRecord.GetHex();
        Console.WriteLine(testCase);
        Console.WriteLine(hex);
        Assert.That(signer.Verify(nodeRecord), Is.True);
        Assert.That(nodeRecord.ToRlpBytes(), Is.EqualTo(Bytes.FromHexString(testCase)));
    }

    [Test]
    public void Can_serialize_eth_entry_as_nested_fork_id_list()
    {
        byte[] forkHash = [1, 2, 3, 4];
        const long nextBlock = 0x0506;
        byte[] expectedEntryBytes = Bytes.FromHexString("83657468c9c88401020304820506");

        Ecdsa ecdsa = new();
        PrivateKey privateKey = new(TestPrivateKey);
        NodeRecordSigner signer = new(ecdsa, privateKey);
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new EthEntry(forkHash, nextBlock));
        nodeRecord.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        signer.Sign(nodeRecord);

        byte[] recordBytes = nodeRecord.ToRlpBytes();
        Assert.That(recordBytes.AsSpan().IndexOf(expectedEntryBytes), Is.GreaterThanOrEqualTo(0));

        NodeRecord decoded = NodeRecord.FromBytes(recordBytes, ecdsa);
        ForkId? forkId = decoded.GetValue<ForkId>(EnrContentKey.Eth);

        Assert.That(forkId, Is.Not.Null);
        Assert.That(forkId.Value.ForkHash, Is.EqualTo(forkHash));
        Assert.That(forkId.Value.NextBlock, Is.EqualTo(nextBlock));
    }

    [TestCaseSource(nameof(InvalidRecordByteCases))]
    public void FromBytes_throws_when_record_bytes_are_invalid(Func<byte[]> createRecordBytes)
        => Assert.That(() => NodeRecord.FromBytes(createRecordBytes()), Throws.TypeOf<RlpException>());

    [Test]
    public void Cannot_verify_when_signature_missing()
    {
        PrivateKey privateKey = new(TestPrivateKey);
        NodeRecordSigner signer = new(new Ecdsa(), privateKey);
        NodeRecord nodeRecord = new();
        Assert.Throws<Exception>(() => _ = signer.Verify(nodeRecord));
    }

    private static RlpStream CreateRecord(params (string Key, Action<RlpStream> EncodeValue, int ValueLength)[] entries)
    {
        byte[] signature = new byte[64];
        int contentLength = Rlp.LengthOf(signature) + Rlp.LengthOf(1UL);
        foreach ((string key, _, int valueLength) in entries)
        {
            contentLength += Rlp.LengthOf(key) + valueLength;
        }

        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(signature);
        rlpStream.Encode(1UL);
        foreach ((string key, Action<RlpStream> encodeValue, _) in entries)
        {
            rlpStream.Encode(key);
            encodeValue(rlpStream);
        }

        rlpStream.Position = 0;
        return rlpStream;
    }

    private static IEnumerable<TestCaseData> InvalidRecordRlpCases()
    {
        yield return InvalidRecordCase(
            CreateNonSequenceRecord,
            typeof(RlpException),
            "Throws_when_record_is_not_a_sequence");

        yield return InvalidRecordCase(
            CreateRecordWithDeclaredLengthOverLimit,
            typeof(RlpException),
            "Throws_when_declared_record_payload_is_bigger_than_300_bytes");

        yield return InvalidRecordCase(
            CreateEncodedRecordOverLimit,
            typeof(RlpException),
            "Throws_when_encoded_record_is_bigger_than_300_bytes");

        yield return InvalidRecordCase(
            CreateRecordWithOversizedSignature,
            typeof(RlpLimitException),
            "Throws_when_signature_is_too_long");

        yield return InvalidRecordCase(
            static () => CreateRecord(
                (EnrContentKey.Udp, static stream => stream.Encode(30303), Rlp.LengthOf(30303)),
                (EnrContentKey.Id, static stream => stream.Encode("v4"), Rlp.LengthOf("v4"))),
            typeof(RlpException),
            "Throws_when_keys_are_not_sorted");

        yield return InvalidRecordCase(
            static () => CreateRecord(
                (EnrContentKey.Id, static stream => stream.Encode("v4"), Rlp.LengthOf("v4")),
                (EnrContentKey.Id, static stream => stream.Encode("v4"), Rlp.LengthOf("v4"))),
            typeof(RlpException),
            "Throws_when_keys_are_duplicated");

        yield return InvalidRecordCase(
            static () => CreateRecord(
                ("z", static stream => stream.Encode(Array.Empty<byte>()), Rlp.LengthOf(Array.Empty<byte>()))),
            typeof(RlpException),
            "Throws_when_id_is_missing");

        yield return InvalidRecordCase(
            static () => CreateRecord(
                (EnrContentKey.Id, static stream => stream.Encode(string.Empty), Rlp.LengthOf(string.Empty))),
            typeof(RlpException),
            "Throws_when_id_is_empty");

        yield return InvalidRecordCase(
            static () => CreateRecord(
                (EnrContentKey.Id, static stream => stream.Encode("V4"), Rlp.LengthOf("V4"))),
            typeof(RlpException),
            "Throws_when_id_has_wrong_case");

        yield return InvalidRecordCase(
            static () => CreateRecord(
                (EnrContentKey.Id, static stream => stream.Encode("v5"), Rlp.LengthOf("v5"))),
            typeof(RlpException),
            "Throws_when_id_is_not_v4");
    }

    private static IEnumerable<TestCaseData> InvalidRecordByteCases()
    {
        yield return new TestCaseData((Func<byte[]>)CreateRecordWithTrailingBytes)
            .SetName("FromBytes_throws_when_record_has_trailing_bytes");

        yield return new TestCaseData((Func<byte[]>)CreateRecordWithUnrecoverableSignature)
            .SetName("FromBytes_throws_when_signature_cannot_recover");

        yield return new TestCaseData((Func<byte[]>)CreateRecordWithInvalidSignature)
            .SetName("FromBytes_throws_when_signature_does_not_match_public_key");
    }

    private static RlpStream CreateNonSequenceRecord()
    {
        RlpStream rlpStream = new(Rlp.LengthOf(EnrContentKey.Id));
        rlpStream.Encode(EnrContentKey.Id);
        rlpStream.Position = 0;
        return rlpStream;
    }

    private static RlpStream CreateRecordWithDeclaredLengthOverLimit()
    {
        Span<byte> bytes = Bytes.FromHexString("540b38f8b160f23b1cd30972338a09ba4a296e2f0cb63f76ce0b38201a8dd9aa2a9c306370904877ddab397f7845ff67ea0a1dbf094b86794bb5d739e6bda891a486098717e2fb744e04c4665d307a590c6e4141a3805de15eb1eb62b0c6ff0aa75db9559545e294e158b7dc9e4a118cf0c2c6259af2df7c1742731064df376182b2df2e714df9e87ec6492effb4de8e2a92bdb405bbe3d8ddf96622bbcb11592fdb2600356cb39fd2c36cac66e19cd1b136ac3be993ef0ed07905d95f16cc67cfbe9bc7c180b90023d55d9218bef9e052c9f655a5c2464abe24271cc1dc2f3df7d3abd926f4657b724b0435868a09f7136ec115cbc3ec1c675972315e4cc140907e4772c118d51917b16a00a7809cfa767ea3ae5557c0b972c37f77d85062910e3e15ae4613cac178220deadc6d729da20c85166e8532d8f88cd246e6102f5268cd5e29796d06713d0f684e096e5edfca6b6c7adf9e51e10f5140d92216123eb31984a61d5a9caf904a2e12f3f479b27d75aeafe0d35b8995468aa12ba7d8f17fbb0aeea63b4d2c74e43b60e06a62bed5ee3ae34f5d74465087b5932865a2cb41f1fdaa9b2b9143fe1923d7f0e4b18a3139ee469df8e6cfea46101674e5fde4c84f9f9d77dee3d0545897a69d9eb42ccc48b699baa9d932dc36783da3580a78abc68b20a1f8bda90afb5ed78a9ac46e63792182b7669e4daaf3ca7e9b5690a3bbf0a184b14470f899582d4a0423897a295441b4bf27db3d2e8adf41824538942198a064bc489fd0936e11f5266146432a8efc992e1d304a4ab6bf661fa1ab3b59d1f14155c5e6a8d1e9eed717bee86a9b6bdabde638c0d1");
        RlpStream rlpStream = new(600);
        rlpStream.StartSequence(500);
        rlpStream.Encode(bytes[..500]);
        rlpStream.Position = 0;
        return rlpStream;
    }

    private static RlpStream CreateEncodedRecordOverLimit()
    {
        byte[] filler = FindFillerForOversizedEncodedRecord();
        return CreateRecord(
            (EnrContentKey.Id, static stream => stream.Encode("v4"), Rlp.LengthOf("v4")),
            ("z", stream => stream.Encode(filler), Rlp.LengthOf(filler)));
    }

    private static RlpStream CreateRecordWithOversizedSignature()
        => CreateRecordWithSignatureLength(66,
            (EnrContentKey.Id, static stream => stream.Encode("v4"), Rlp.LengthOf("v4")));

    private static RlpStream CreateRecordWithSignatureLength(
        int signatureLength,
        params (string Key, Action<RlpStream> EncodeValue, int ValueLength)[] entries)
    {
        byte[] signature = new byte[signatureLength];
        int contentLength = Rlp.LengthOf(signature) + Rlp.LengthOf(1UL);
        foreach ((string key, _, int valueLength) in entries)
        {
            contentLength += Rlp.LengthOf(key) + valueLength;
        }

        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(signature);
        rlpStream.Encode(1UL);
        foreach ((string key, Action<RlpStream> encodeValue, _) in entries)
        {
            rlpStream.Encode(key);
            encodeValue(rlpStream);
        }

        rlpStream.Position = 0;
        return rlpStream;
    }

    private static byte[] CreateRecordWithTrailingBytes()
    {
        byte[] recordBytes = Bytes.FromHexString(
            "f897b840421561b4ed5de28a7100e0a5005ecc0ba6ba6cc18528061e811704c8794fec965cba63831051d134bdc801c0c90d31a30d241074095311ffe6628d5545478b770a83657468c7c68496516d06808269648276348269708436ed0a0a89736563703235366b31a103f5c110132b0374805d4453f55577cc9c58bb1a08f822b9b3722132e3095f69728374637082765f8375647082765f");
        return [.. recordBytes, 0x80];
    }

    private static byte[] CreateRecordWithUnrecoverableSignature()
    {
        byte[] publicKey = new byte[CompressedPublicKey.LengthInBytes];
        RlpStream rlpStream = CreateRecord(
            (EnrContentKey.Id, static stream => stream.Encode("v4"), Rlp.LengthOf("v4")),
            (EnrContentKey.SecP256k1, stream => stream.Encode(publicKey), Rlp.LengthOf(publicKey)));

        return rlpStream.Data.AsSpan().ToArray();
    }

    private static byte[] CreateRecordWithInvalidSignature()
    {
        Ecdsa ecdsa = new();
        PrivateKey privateKey = new(TestPrivateKey);
        NodeRecordSigner signer = new(ecdsa, privateKey);
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        signer.Sign(nodeRecord);

        byte[] recordBytes = nodeRecord.ToRlpBytes().AsSpan().ToArray();
        recordBytes[4] ^= 0x01;
        return recordBytes;
    }

    private static TestCaseData InvalidRecordCase(Func<RlpStream> createRecord, Type exceptionType, string name)
        => new TestCaseData(createRecord, exceptionType).SetName(name);

    private static byte[] FindFillerForOversizedEncodedRecord()
    {
        for (int i = 0; i <= 300; i++)
        {
            byte[] filler = new byte[i];
            int contentLength =
                Rlp.LengthOf(new byte[64]) +
                Rlp.LengthOf(1UL) +
                Rlp.LengthOf(EnrContentKey.Id) +
                Rlp.LengthOf("v4") +
                Rlp.LengthOf("z") +
                Rlp.LengthOf(filler);
            if (contentLength <= 300 && Rlp.LengthOfSequence(contentLength) > 300)
            {
                return filler;
            }
        }

        throw new InvalidOperationException("Could not create oversized ENR fixture.");
    }
}
