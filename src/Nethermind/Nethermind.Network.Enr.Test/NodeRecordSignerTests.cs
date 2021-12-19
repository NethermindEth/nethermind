using System;
using System.Buffers.Binary;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using NUnit.Framework;

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
            .Replace("enr:", "")
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
        nodeRecord.Sequence = 1; // override

        string enrString = signer.GetEnrString(nodeRecord);
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
            .Replace("enr:", "")
            .Replace('-', '+')
            .Replace('_', '/') + /*padding*/ "=";
        byte[] expected = Convert.FromBase64String(expectedEnrStringNonRfcBase64);
        string expectedHexString = expected.ToHexString();
        Console.WriteLine("expected: " + expectedHexString);

        Ecdsa ecdsa = new();
        PrivateKey privateKey = new("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        PublicKey expectedPublicKey = privateKey.PublicKey;
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

        nodeRecord.Sequence = 1; // override

        signer.Sign(nodeRecord);
        Assert.AreEqual(signer.Verify(nodeRecord), compressedPublicKey);
    }
}
