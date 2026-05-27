// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv5;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using System;
using System.Net;

namespace Nethermind.Network.Discovery.Test.Discv5;

public class Discv5CodecTests
{
    private static readonly byte[] NodeAId = Bytes.FromHexString("0xaaaa8419e9f49d0083561b48287df592939a8d19947d8c0ef88f2a4856a69fbb");
    private static readonly byte[] NodeBId = Bytes.FromHexString("0xbbbb9d047f0488c0b5a93c1c3f2d8bafc7c8ff337024a55434a0d0555de64db9");
    private const string GethNodeAPrivateKey = "0xeef77acb6c6a6eebc5b363a475ac583ec7eccdb42b6481424c60f59aa326547f";
    private const string GethNodeBPrivateKey = "0x66fb62bfbd66b9177a138c1e5cddbe4f7c30c343e94e68df8769459cb1cde628";

    [Test]
    public void CompressedAgreement_Matches_Devp2p_Vector()
    {
        CompressedPublicKey publicKey = new("0x039961e4c2356d61bedb83052c115d311acb3a96f5777296dcf297351130266231");
        PrivateKey privateKey = new("0xfb757dc581730490a1d7a00deea65e9b1936924caaea8f44d476014856b68736");

        byte[] sharedSecret = SecP256k1Agreement.AgreeCompressed(publicKey, privateKey);

        Assert.That(sharedSecret.ToHexString(true), Is.EqualTo("0x033b11a2a1f214567e1537ce5e509ffd9b21373247f2a3ff6841f4976f53165e7e"));
    }

    [Test]
    public void KeyDerivation_Matches_Devp2p_Vector()
    {
        CompressedPublicKey destinationPublicKey = new("0x0317931e6e0840220642f230037d285d122bc59063221ef3226b1f403ddc69ca91");
        PrivateKey ephemeralPrivateKey = new("0xfb757dc581730490a1d7a00deea65e9b1936924caaea8f44d476014856b68736");
        byte[] secret = SecP256k1Agreement.AgreeCompressed(destinationPublicKey, ephemeralPrivateKey);
        byte[] challengeData = Bytes.FromHexString("0x000000000000000000000000000000006469736376350001010102030405060708090a0b0c00180102030405060708090a0b0c0d0e0f100000000000000000");

        (byte[] initiatorKey, byte[] recipientKey) = Discv5PacketCodec.DeriveKeysForTest(secret, NodeAId, NodeBId, challengeData);

        Assert.That(initiatorKey.ToHexString(true), Is.EqualTo("0xdccc82d81bd610f4f76d3ebe97a40571"));
        Assert.That(recipientKey.ToHexString(true), Is.EqualTo("0xac74bb8773749920b0d3a8881c173ec5"));
    }

    [Test]
    public void IdNonceSignature_Matches_Devp2p_Vector()
    {
        PrivateKey staticKey = new("0xfb757dc581730490a1d7a00deea65e9b1936924caaea8f44d476014856b68736");
        byte[] challengeData = Bytes.FromHexString("0x000000000000000000000000000000006469736376350001010102030405060708090a0b0c00180102030405060708090a0b0c0d0e0f100000000000000000");
        byte[] ephemeralPublicKey = Bytes.FromHexString("0x039961e4c2356d61bedb83052c115d311acb3a96f5777296dcf297351130266231");
        byte[] signingHash = Discv5PacketCodec.CalculateIdSignatureHashForTest(challengeData, ephemeralPublicKey, NodeBId);

        Signature signature = new Ecdsa().Sign(staticKey, new ValueHash256(signingHash));

        Assert.That(signature.Bytes.ToArray().ToHexString(true), Is.EqualTo("0x94852a1e2318c4e5e9d422c98eaf19d1d90d876b29cd06ca7cb7546d0fff7b484fe86c09a064fe72bdbef73ba8e9c34df0cd2b53e9d65528c2c7f336d5dfc6e6"));
    }

    [Test]
    public void PacketCodec_Decodes_PingPacket_Devp2p_Vector()
    {
        byte[] packetBytes = Bytes.FromHexString(
            "0x00000000000000000000000000000000088b3d4342774649325f313964a39e55" +
            "ea96c005ad52be8c7560413a7008f16c9e6d2f43bbea8814a546b7409ce783d3" +
            "4c4f53245d08dab84102ed931f66d1492acb308fa1c6715b9d139b81acbdcc");

        bool decoded = Discv5PacketCodec.TryDecode(packetBytes, NodeBId, out Discv5Packet packet);
        bool decrypted = Discv5PacketCodec.TryDecryptMessageForTest(packet, new byte[16], out Discv5Message message);

        Assert.That(decoded, Is.True);
        Assert.That(packet.Flag, Is.EqualTo(Discv5PacketFlag.Ordinary));
        Assert.That(packet.AuthData, Is.EqualTo(NodeAId));
        Assert.That(decrypted, Is.True);
        Assert.That(message, Is.InstanceOf<Discv5Ping>());
        Discv5Ping ping = (Discv5Ping)message;
        Assert.That(ping.RequestId, Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
        Assert.That(ping.EnrSequence, Is.EqualTo(2));
    }

    [Test]
    public void PacketCodec_Decodes_WhoAreYou_GoEthereum_Vector()
    {
        byte[] packetBytes = Bytes.FromHexString(
            "0x00000000000000000000000000000000088b3d434277464933a1ccc59f5967ad" +
            "1d6035f15e528627dde75cd68292f9e6c27d6b66c8100a873fcbaed4e16b8d");
        byte[] challengeData = Bytes.FromHexString("0x000000000000000000000000000000006469736376350001010102030405060708090a0b0c00180102030405060708090a0b0c0d0e0f100000000000000000");

        bool decoded = Discv5PacketCodec.TryDecode(packetBytes, NodeBId, out Discv5Packet packet);
        Discv5PacketCodec codec = CreateCodec(new PrivateKey(GethNodeBPrivateKey));
        Discv5Challenge challenge = codec.DecodeWhoAreYou(packet);

        Assert.That(decoded, Is.True);
        Assert.That(packet.Flag, Is.EqualTo(Discv5PacketFlag.WhoAreYou));
        Assert.That(challenge.RequestNonce.ToHexString(true), Is.EqualTo("0x0102030405060708090a0b0c"));
        Assert.That(challenge.IdNonce.ToHexString(true), Is.EqualTo("0x0102030405060708090a0b0c0d0e0f10"));
        Assert.That(challenge.EnrSequence, Is.Zero);
        Assert.That(challenge.ChallengeData, Is.EqualTo(challengeData));
    }

    [TestCase(
        "0x00000000000000000000000000000000088b3d4342774649305f313964a39e55" +
        "ea96c005ad521d8c7560413a7008f16c9e6d2f43bbea8814a546b7409ce783d3" +
        "4c4f53245d08da4bb252012b2cba3f4f374a90a75cff91f142fa9be3e0a5f3ef" +
        "268ccb9065aeecfd67a999e7fdc137e062b2ec4a0eb92947f0d9a74bfbf44dfb" +
        "a776b21301f8b65efd5796706adff216ab862a9186875f9494150c4ae06fa4d1" +
        "f0396c93f215fa4ef524f1eadf5f0f4126b79336671cbcf7a885b1f8bd2a5d83" +
        "9cf8",
        1UL,
        "0x4f9fac6de7567d1e3b1241dffe90f662",
        "0x000000000000000000000000000000006469736376350001010102030405060708090a0b0c00180102030405060708090a0b0c0d0e0f100000000000000001",
        false)]
    [TestCase(
        "0x00000000000000000000000000000000088b3d4342774649305f313964a39e55" +
        "ea96c005ad539c8c7560413a7008f16c9e6d2f43bbea8814a546b7409ce783d3" +
        "4c4f53245d08da4bb23698868350aaad22e3ab8dd034f548a1c43cd246be9856" +
        "2fafa0a1fa86d8e7a3b95ae78cc2b988ded6a5b59eb83ad58097252188b902b2" +
        "1481e30e5e285f19735796706adff216ab862a9186875f9494150c4ae06fa4d1" +
        "f0396c93f215fa4ef524e0ed04c3c21e39b1868e1ca8105e585ec17315e755e6" +
        "cfc4dd6cb7fd8e1a1f55e49b4b5eb024221482105346f3c82b15fdaae36a3bb1" +
        "2a494683b4a3c7f2ae41306252fed84785e2bbff3b022812d0882f06978df84a" +
        "80d443972213342d04b9048fc3b1d5fcb1df0f822152eced6da4d3f6df27e70e" +
        "4539717307a0208cd208d65093ccab5aa596a34d7511401987662d8cf62b1394" +
        "71",
        0UL,
        "0x53b1c075f41876423154e157470c2f48",
        "0x000000000000000000000000000000006469736376350001010102030405060708090a0b0c00180102030405060708090a0b0c0d0e0f100000000000000000",
        true)]
    public void PacketCodec_Decodes_PingHandshake_GoEthereum_Vectors(
        string packetHex,
        ulong challengeEnrSequence,
        string expectedReadKeyHex,
        string challengeDataHex,
        bool includesRecord)
    {
        byte[] packetBytes = Bytes.FromHexString(packetHex);
        Discv5Challenge challenge = new(
            Bytes.FromHexString("0x0102030405060708090a0b0c"),
            Bytes.FromHexString("0x0102030405060708090a0b0c0d0e0f10"),
            challengeEnrSequence,
            Bytes.FromHexString(challengeDataHex));
        Discv5PacketCodec codec = CreateCodec(new PrivateKey(GethNodeBPrivateKey));
        NodeRecord? knownRecord = includesRecord ? null : CreateNodeRecord(new PrivateKey(GethNodeAPrivateKey));

        bool decoded = Discv5PacketCodec.TryDecode(packetBytes, NodeBId, out Discv5Packet packet);
        bool decrypted = codec.TryDecryptHandshake(packet, challenge, knownRecord, out Discv5Session session, out Discv5Message message, out NodeRecord? nodeRecord);

        Assert.That(decoded, Is.True);
        Assert.That(packet.Flag, Is.EqualTo(Discv5PacketFlag.Handshake));
        Assert.That(decrypted, Is.True);
        Assert.That(session.ReadKey.ToHexString(true), Is.EqualTo(expectedReadKeyHex));
        Assert.That(message, Is.InstanceOf<Discv5Ping>());
        Discv5Ping ping = (Discv5Ping)message;
        Assert.That(ping.RequestId, Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
        Assert.That(ping.EnrSequence, Is.EqualTo(1));
        Assert.That(nodeRecord is not null, Is.EqualTo(includesRecord));
    }

    [Test]
    public void MessageCodec_Roundtrips_FindNode()
    {
        Discv5FindNode message = new([0, 0, 0, 1], [255, 254, 256]);

        Discv5Message decoded = Discv5MessageCodec.Decode(Discv5MessageCodec.Encode(message));

        Assert.That(decoded, Is.InstanceOf<Discv5FindNode>());
        Discv5FindNode decodedFindNode = (Discv5FindNode)decoded;
        Assert.That(decodedFindNode.RequestId, Is.EqualTo(message.RequestId));
        Assert.That(decodedFindNode.Distances, Is.EqualTo(message.Distances));
    }

    [Test]
    public void MessageCodec_Rejects_Nodes_With_Invalid_Enr()
    {
        byte[] invalidRecord = new byte[304];
        invalidRecord[0] = 0xf9;
        invalidRecord[1] = 0x01;
        invalidRecord[2] = 0x2d;

        Rlp data = Rlp.Encode(
            Rlp.Encode(new byte[] { 1 }),
            Rlp.Encode(1),
            Rlp.Encode(new Rlp(invalidRecord)));
        byte[] message = new byte[data.Length + 1];
        message[0] = (byte)Discv5MessageType.Nodes;
        data.Bytes.CopyTo(message.AsSpan(1));

        Assert.That(() => Discv5MessageCodec.Decode(message), Throws.TypeOf<RlpException>());
    }

    private static Discv5PacketCodec CreateCodec(PrivateKey privateKey)
        => new(
            new InsecureProtectedPrivateKey(privateKey),
            new TestNodeRecordProvider(privateKey),
            new CryptoRandom(),
            new EthereumEcdsa(0));

    private static NodeRecord CreateNodeRecord(PrivateKey privateKey)
    {
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(IdEntry.Instance);
        nodeRecord.SetEntry(new IpEntry(IPAddress.Loopback));
        nodeRecord.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        nodeRecord.EnrSequence = 1;
        new NodeRecordSigner(new EthereumEcdsa(0), privateKey).Sign(nodeRecord);
        return nodeRecord;
    }

    private sealed class TestNodeRecordProvider(PrivateKey privateKey) : INodeRecordProvider
    {
        public NodeRecord Current { get; } = CreateNodeRecord(privateKey);
    }
}
