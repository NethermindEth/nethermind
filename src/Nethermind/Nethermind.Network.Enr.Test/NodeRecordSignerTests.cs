using System;
using System.Buffers.Binary;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using NUnit.Framework.Internal.Commands;

namespace Nethermind.Network.Enr.Test;

public class NodeRecordSignerTests
{
    [SetUp]
    public void Setup()
    {
    }

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
        PrivateKey privateKey = new("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        NodeRecordSigner signer = new(ecdsa, privateKey);
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new IpEntry(
            new IPAddress(Bytes.FromHexString("7f000001"))));
        nodeRecord.SetEntry(new UdpEntry(
            BinaryPrimitives.ReadInt16BigEndian(Bytes.FromHexString("765f"))));
        nodeRecord.SetEntry(new Secp256K1Entry(
            // new CompressedPublicKey("03a448f24c6d18e575453db13171562b71999873db5b286df957af199ec94617f7")));
            new CompressedPublicKey("03ca634cae0d49acb401d8a4c6b6fe8c55b70d115bf400769cc1400f3258cd3138")));
        nodeRecord.EnrSequence = 1; // override
        
        signer.Sign(nodeRecord);
        string enrString = nodeRecord.EnrString;
        Assert.AreEqual(expectedEnrString, enrString);
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
        PrivateKey privateKey = new("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");

        NodeRecordSigner signer = new(ecdsa, privateKey);
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new IpEntry(
            new IPAddress(Bytes.FromHexString("7f000001"))));
        nodeRecord.SetEntry(new UdpEntry(
            BinaryPrimitives.ReadInt16BigEndian(Bytes.FromHexString("765f"))));

        CompressedPublicKey compressedPublicKey =
            new("03ca634cae0d49acb401d8a4c6b6fe8c55b70d115bf400769cc1400f3258cd3138");
        nodeRecord.SetEntry(new Secp256K1Entry(
            // new CompressedPublicKey("03a448f24c6d18e575453db13171562b71999873db5b286df957af199ec94617f7")));
            compressedPublicKey));

        nodeRecord.EnrSequence = 1; // override

        signer.Sign(nodeRecord);
        Assert.IsTrue(signer.Verify(nodeRecord));
    }

    [TestCase("f897b840421561b4ed5de28a7100e0a5005ecc0ba6ba6cc18528061e811704c8794fec965cba63831051d134bdc801c0c90d31a30d241074095311ffe6628d5545478b770a83657468c7c68496516d06808269648276348269708436ed0a0a89736563703235366b31a103f5c110132b0374805d4453f55577cc9c58bb1a08f822b9b3722132e3095f69728374637082765f8375647082765f")]
    [TestCase("f897b8406fb9316953b51793ee43316fe14f2d0ac0b356b86815175c6d231840bd6f24e504bfa6492ccc1f4b0853b02ae44fbee861f52044dd08e4a23edf6187ea5e46e71583657468c7c68420c327fc80826964827634826970847f00000189736563703235366b31a102ba4be3a4095b23fe90a850709394476bf23c9788ad124325a6163f342e05a7308374637082765f8375647082765f")]
    [TestCase("f89fb8401d2ab9d1937f7d3524feec8edb45e3abc4e4a01ca227615502bcad2cd68eaf804fc5865f6a5551bd5c39f56ee4d4c005c69be3efc44f2a9ff312d71de13a62de8207ab83657468c7c6843de1adaf808269648276348269708467e4b73289736563703235366b31a102bb8f962e961a1d82dac4bc32b71e491da35bcd69e18bec31aba9b9fadd0e1a1184736e6170c08374637082765f8375647082765f")]
    public void Can_deserialize_and_verify_real_world_cases(string testCase)
    {
        Ecdsa ecdsa = new();
        PrivateKey privateKey = new("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        NodeRecordSigner signer = new(ecdsa, privateKey);
        RlpStream rlpStream = Bytes.FromHexString(testCase).AsRlpStream();
        NodeRecord nodeRecord = signer.Deserialize(rlpStream);
        string hex = nodeRecord.GetHex();
        Console.WriteLine(testCase);
        Console.WriteLine(hex);
        Assert.IsTrue(signer.Verify(nodeRecord));
    }
    
    
    [Test]
    public void Cannot_verify_when_signature_missing()
    {
        PrivateKey privateKey = new("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        NodeRecordSigner signer = new (new Ecdsa(), privateKey);
        NodeRecord nodeRecord = new ();
        Assert.Throws<Exception>(() => _ = signer.Verify(nodeRecord));
    }
}
