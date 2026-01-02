using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class PacketBuilderTests
{
    private Mock<IIdentityManager> _identityManagerMock;
    private Mock<IRequestManager> _requestManagerMock;
    private Mock<ILogger<PacketBuilderTests>> _loggerMock;
    private Mock<ILoggerFactory> _loggerFactoryMock;
    private IPacketBuilder _packetBuilder;
    private IPacketProcessor _packetProcessor;
    private IIdentityVerifier _identityVerifier;

    [SetUp]
    public void SetUp()
    {
        _identityManagerMock = new Mock<IIdentityManager>();
        _requestManagerMock = new Mock<IRequestManager>();
        _loggerMock = new Mock<ILogger<PacketBuilderTests>>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(_loggerMock.Object);

        _identityVerifier = new IdentityVerifierV4();
    }

    [Test]
    public void BuildRandomOrdinaryPacket_Should_Return_Valid_Packet()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var nodeId = _identityVerifier.GetNodeIdFromRecord(enrRecord);

        _identityManagerMock
            .SetupGet(x => x.Record.NodeId)
            .Returns(nodeId);

        _packetBuilder = new PacketBuilder(_identityManagerMock.Object, new AesCrypto(), _requestManagerMock.Object, _loggerFactoryMock.Object);
        _packetProcessor = new PacketProcessor(_identityManagerMock.Object, new AesCrypto());

        var result = _packetBuilder.BuildRandomOrdinaryPacket(nodeId);
        var parsed = _packetProcessor.TryGetStaticHeader(result.Packet, out StaticHeader? staticHeader);
        var maskingIv = _packetProcessor.GetMaskingIv(result.Packet);

        Assert.IsNotNull(parsed);
        Assert.IsTrue(parsed);
        Assert.IsInstanceOf<PacketResult>(result);
        Assert.IsTrue(result.Header.Flag == staticHeader!.Flag);
        Assert.IsTrue(result.Header.AuthData.SequenceEqual(staticHeader.AuthData));
        Assert.IsTrue(result.Header.Version.SequenceEqual(staticHeader.Version));
        Assert.IsTrue(result.Header.Nonce.SequenceEqual(staticHeader.Nonce));
        Assert.IsTrue(result.Header.AuthDataSize == staticHeader.AuthDataSize);
        Assert.IsTrue(maskingIv.SequenceEqual(result.Packet[..16]));

        Assert.IsTrue(result.Header.Nonce.Length == PacketConstants.NonceSize);
        Assert.IsTrue(result.Header.AuthData.Length == PacketConstants.Ordinary);
        Assert.IsTrue(result.Header.AuthDataSize == PacketConstants.Ordinary);
        Assert.IsTrue(result.Header.Version.Length == PacketConstants.VersionSize);
        Assert.IsTrue(maskingIv.Length == PacketConstants.MaskingIvSize);
    }

    [Test]
    public void BuildWhoAreYouPacketWithoutEnr_Should_Return_Valid_Packet()
    {
        var destNodeId = Convert.FromHexString("F92B82F11AF5ED0959135CDE8E64B626CAC4F16D05E43087224DEED25D1DBD72");
        var packetNonce = RandomUtility.GenerateRandomData(12);
        var maskingIv = Convert.FromHexString("EE1A7C1BB363686AACDAF6E84C66EB7A");

        _identityManagerMock
            .SetupGet(x => x.Record.NodeId)
            .Returns(destNodeId);

        _packetBuilder = new PacketBuilder(_identityManagerMock.Object, new AesCrypto(), _requestManagerMock.Object, _loggerFactoryMock.Object);
        _packetProcessor = new PacketProcessor(_identityManagerMock.Object, new AesCrypto());

        var result = _packetBuilder.BuildWhoAreYouPacketWithoutEnr(destNodeId, packetNonce, maskingIv);
        var parsed = _packetProcessor.TryGetStaticHeader(result.Packet, out StaticHeader? staticHeader);
        var maskingIvResult = _packetProcessor.GetMaskingIv(result.Packet);

        Assert.IsNotNull(result);
        Assert.True(parsed);
        Assert.IsInstanceOf<PacketResult>(result);
        Assert.IsTrue(result.Header.Flag == staticHeader.Flag);
        Assert.IsTrue(result.Header.AuthData.SequenceEqual(staticHeader.AuthData));
        Assert.IsTrue(result.Header.Version.SequenceEqual(staticHeader.Version));
        Assert.IsTrue(result.Header.Nonce.SequenceEqual(staticHeader.Nonce));
        Assert.IsTrue(maskingIvResult.SequenceEqual(result.Packet[..16]));

        Assert.IsTrue(result.Header.AuthDataSize == staticHeader.AuthDataSize);
        Assert.IsTrue(result.Header.Nonce.Length == PacketConstants.NonceSize);
        Assert.IsTrue(result.Header.AuthData.Length == PacketConstants.WhoAreYou);
        Assert.IsTrue(result.Header.AuthDataSize == PacketConstants.WhoAreYou);
        Assert.IsTrue(result.Header.Version.Length == PacketConstants.VersionSize);
        Assert.IsTrue(maskingIvResult.Length == PacketConstants.MaskingIvSize);
    }

    [Test]
    public void BuildWhoAreYouPacket_Should_Return_Valid_Packet()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var destNodeId = Convert.FromHexString("F92B82F11AF5ED0959135CDE8E64B626CAC4F16D05E43087224DEED25D1DBD72");
        var packetNonce = RandomUtility.GenerateRandomData(12);
        var maskingIv = Convert.FromHexString("EE1A7C1BB363686AACDAF6E84C66EB7A");

        _identityManagerMock
            .SetupGet(x => x.Record.NodeId)
            .Returns(destNodeId);

        _packetBuilder = new PacketBuilder(_identityManagerMock.Object, new AesCrypto(), _requestManagerMock.Object, _loggerFactoryMock.Object);
        _packetProcessor = new PacketProcessor(_identityManagerMock.Object, new AesCrypto());

        var result = _packetBuilder.BuildWhoAreYouPacket(destNodeId, packetNonce, enrRecord, maskingIv);
        var parsed = _packetProcessor.TryGetStaticHeader(result.Packet, out StaticHeader? staticHeader);
        var maskingIvResult = _packetProcessor.GetMaskingIv(result.Packet);

        Assert.IsNotNull(result);
        Assert.True(parsed);
        Assert.IsInstanceOf<PacketResult>(result);
        Assert.IsTrue(result.Header.Flag == staticHeader.Flag);
        Assert.IsTrue(result.Header.AuthData.SequenceEqual(staticHeader.AuthData));
        Assert.IsTrue(result.Header.Version.SequenceEqual(staticHeader.Version));
        Assert.IsTrue(result.Header.Nonce.SequenceEqual(staticHeader.Nonce));
        Assert.IsTrue(result.Header.AuthDataSize == staticHeader.AuthDataSize);
        Assert.IsTrue(maskingIvResult.SequenceEqual(result.Packet[..16]));

        Assert.IsTrue(result.Header.Nonce.Length == PacketConstants.NonceSize);
        Assert.IsTrue(result.Header.AuthData.Length == PacketConstants.WhoAreYou);
        Assert.IsTrue(result.Header.AuthDataSize == PacketConstants.WhoAreYou);
        Assert.IsTrue(result.Header.Version.Length == PacketConstants.VersionSize);
        Assert.IsTrue(maskingIvResult.Length == PacketConstants.MaskingIvSize);
    }

    [Test]
    public void BuildHandshakePacket_Should_Return_Valid_Packet()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var idSignature =
            Convert.FromHexString(
                "933468458F4F3DE637D9B84917DEDD8103C4A297A4980B703A727D017B92B2713A95FD12F0AD9264845FC6F1FF29F6E0706075019F43E8C624F4E58F78A6ED8C");
        var ephemeralPubKey =
            Convert.FromHexString("02044862F58DF5D9C43B6A4C4EBC9E9F4D0BFDFB42052265271B5A3A331803725C");
        var destNodeId = Convert.FromHexString("F92B82F11AF5ED0959135CDE8E64B626CAC4F16D05E43087224DEED25D1DBD72");
        var maskingIv = Convert.FromHexString("13E5792577482999855F3C5BE73FC550");
        var messageCount = Convert.FromHexString("00000000");

        _identityManagerMock
            .Setup(x => x.Record.EncodeRecord())
            .Returns(enrRecord.EncodeRecord);

        _identityManagerMock
            .SetupGet(x => x.Record.NodeId)
            .Returns(destNodeId);

        _packetBuilder = new PacketBuilder(_identityManagerMock.Object, new AesCrypto(), _requestManagerMock.Object, _loggerFactoryMock.Object);
        _packetProcessor = new PacketProcessor(_identityManagerMock.Object, new AesCrypto());

        var result = _packetBuilder.BuildHandshakePacket(idSignature, ephemeralPubKey, destNodeId, maskingIv, messageCount);
        var parsed = _packetProcessor.TryGetStaticHeader(result.Packet, out StaticHeader? staticHeader);
        var maskingIvResult = _packetProcessor.GetMaskingIv(result.Packet);

        Assert.IsNotNull(result);
        Assert.True(parsed);
        Assert.IsInstanceOf<PacketResult>(result);
        Assert.IsTrue(result.Header.Flag == staticHeader.Flag);
        Assert.IsTrue(result.Header.AuthData.SequenceEqual(staticHeader.AuthData));
        Assert.IsTrue(result.Header.Version.SequenceEqual(staticHeader.Version));
        Assert.IsTrue(result.Header.Nonce.SequenceEqual(staticHeader.Nonce));
        Assert.IsTrue(result.Header.AuthDataSize == staticHeader.AuthDataSize);
        Assert.IsTrue(maskingIvResult.SequenceEqual(result.Packet[..16]));

        Assert.IsTrue(result.Header.Nonce.Length == PacketConstants.NonceSize);
        Console.WriteLine(result.Header.AuthData.Length);
        Console.WriteLine(PacketConstants.NodeIdSize + PacketConstants.SigSize + PacketConstants.EphemeralKeySize + idSignature.Length + ephemeralPubKey.Length + enrRecord.EncodeRecord().Length);
        Assert.IsTrue(result.Header.AuthData.Length == PacketConstants.NodeIdSize + PacketConstants.SigSize + PacketConstants.EphemeralKeySize + idSignature.Length + ephemeralPubKey.Length + enrRecord.EncodeRecord().Length);
        Assert.IsTrue(result.Header.AuthDataSize == PacketConstants.NodeIdSize + PacketConstants.SigSize + PacketConstants.EphemeralKeySize + idSignature.Length + ephemeralPubKey.Length + enrRecord.EncodeRecord().Length);
        Assert.IsTrue(result.Header.Version.Length == PacketConstants.VersionSize);
        Assert.IsTrue(maskingIvResult.Length == PacketConstants.MaskingIvSize);
    }
}
