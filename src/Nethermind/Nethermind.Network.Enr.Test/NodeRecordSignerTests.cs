using System;
using System.Buffers.Binary;
using System.Buffers.Text;
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

    [Test]
    public void Verify_eip_test_vector()
    {
        string expectedStringUrlSafe = "-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8=";
        string expectedString = expectedStringUrlSafe.Replace('-', '+').Replace('_', '/'); // go back from Base64Url - also added the '=' padding at the end of the previous string

        byte[] expected =  Convert.FromBase64String(expectedString);
        string expectedHexString = expected.ToHexString();
        Console.WriteLine("expected: " + expectedHexString);
        
        Ecdsa ecdsa = new();
        PrivateKey testKey = new("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        NodeRecordSigner signer = new(ecdsa, testKey);
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
        Assert.AreEqual(
            "enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8",
            enrString);
    }
}
