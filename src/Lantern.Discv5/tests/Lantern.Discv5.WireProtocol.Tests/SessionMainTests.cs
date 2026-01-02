using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Moq;
using NBitcoin.Secp256k1;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class SessionMainTests
{
    private Mock<ISessionKeys> mockSessionKeys = null!;
    private Mock<IAesCrypto> mockAesCrypto = null!;
    private Mock<ISessionCrypto> mockSessionCrypto = null!;
    private Mock<ILoggerFactory> mockLoggerFactory;
    private Mock<ILogger<SessionMain>> logger;

    [SetUp]
    public void Setup()
    {
        mockSessionKeys = new Mock<ISessionKeys>();
        mockAesCrypto = new Mock<IAesCrypto>();
        mockSessionCrypto = new Mock<ISessionCrypto>();
        mockLoggerFactory = new Mock<ILoggerFactory>();
        logger = new Mock<ILogger<SessionMain>>();
        logger.Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(), (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()));
        mockLoggerFactory
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(logger.Object);
    }

    [Test]
    public void Test_GenerateIdSignature_ShouldReturnWhenChallengeDataIsNull()
    {
        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        var result = sessionMain.GenerateIdSignature(new byte[32]);
        Assert.IsNull(result);
    }

    [Test]
    public void Test_GenerateIdSignature_ShouldReturnGenerateIdSignature_WhenChallengeDataIsNotNull()
    {
        var signature = RandomUtility.GenerateRandomData(32);
        mockSessionCrypto
            .Setup(x => x.GenerateIdSignature(It.IsAny<ISessionKeys>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(signature);

        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        sessionMain.SetChallengeData(new byte[32], new byte[32]);

        var result = sessionMain.GenerateIdSignature(RandomUtility.GenerateRandomData(32));
        Assert.IsTrue(result.SequenceEqual(signature));
    }

    [Test]
    public void Test_VerifyIdSignature_ShouldReturnFalse_WhenChallengeDataIsNull()
    {
        var handShakePacket = new HandshakePacketBase(new byte[32], new byte[32], new byte[32], new byte[32]);
        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        var result = sessionMain.VerifyIdSignature(handShakePacket, new byte[32], new byte[32]);
        Assert.IsFalse(result);
    }

    [Test]
    public void Test_VerifyIdSignature_ShouldVerifyIdSignature_WhenChallengeDataIsSet()
    {
        mockSessionCrypto
            .Setup(x => x.VerifyIdSignature(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(),
                It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<Context>()))
            .Returns(true);

        var handShakePacket = new HandshakePacketBase(new byte[32], new byte[32], new byte[32], new byte[32]);
        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        sessionMain.SetChallengeData(new byte[32], new byte[32]);
        var result = sessionMain.VerifyIdSignature(handShakePacket, new byte[32], new byte[32]);
        Assert.IsTrue(result);
    }

    [Test]
    public void Test_EncryptMessageWithNewKeys_ShouldReturnNull_WhenChallengeDataIsNull()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        var result = sessionMain.EncryptMessageWithNewKeys(enrRecord, staticHeader, null, null, null);
        Assert.IsNull(result);
    }

    [Test]
    public void Test_EncryptMessageWithNewKeys_ShouldReturnNull_WhenChallengeDataIsSet()
    {
        var encryptedMessage = RandomUtility.GenerateRandomData(32);
        mockSessionCrypto
            .Setup(x => x.GenerateSessionKeys(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new SharedKeys(new byte[32]));
        mockAesCrypto
            .Setup(x => x.AesGcmEncrypt(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(encryptedMessage);

        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        sessionMain.SetChallengeData(new byte[32], new byte[32]);
        var result = sessionMain.EncryptMessageWithNewKeys(enrRecord, staticHeader, null, null, null);
        Assert.IsTrue(result.SequenceEqual(encryptedMessage));
        Assert.AreEqual(BitConverter.GetBytes(1), sessionMain.MessageCount);
    }

    [Test]
    public void Test_DecryptMessageWithNewKeys_ShouldReturnNull_WhenHandShakeSrcIdIsNull()
    {
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var handshakePacket = new HandshakePacketBase(new byte[32], new byte[32], null, new byte[32]);
        mockSessionCrypto
            .Setup(x => x.GenerateSessionKeys(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new SharedKeys(new byte[32]));

        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        var result = sessionMain.DecryptMessageWithNewKeys(staticHeader, null, null, handshakePacket, null);
        Assert.IsNull(result);
    }

    [Test]
    public void Test_DecryptMessageWithNewKeys_ShouldReturnNull_WhenChallengeDataIsNull()
    {
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var handshakePacket = new HandshakePacketBase(new byte[32], new byte[32], new byte[32], new byte[32]);
        mockSessionCrypto
            .Setup(x => x.GenerateSessionKeys(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new SharedKeys(new byte[32]));

        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        var result = sessionMain.DecryptMessageWithNewKeys(staticHeader, null, null, handshakePacket, null);
        Assert.IsNull(result);
    }

    [Test]
    public void Test_DecryptMessageWithNewKeys_ShouldReturnDecryptedMessage_WhenChallengeDataIsSet()
    {
        var decryptedMessage = RandomUtility.GenerateRandomData(32);
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var handshakePacket = new HandshakePacketBase(new byte[32], new byte[32], new byte[32], new byte[32]);

        mockSessionCrypto
            .Setup(x => x.GenerateSessionKeys(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new SharedKeys(new byte[32]));
        mockAesCrypto
            .Setup(x => x.AesGcmDecrypt(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(decryptedMessage);

        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        sessionMain.SetChallengeData(new byte[32], new byte[32]);
        var result = sessionMain.DecryptMessageWithNewKeys(staticHeader, null, null, handshakePacket, null);
        Assert.IsTrue(result.SequenceEqual(decryptedMessage));
        Assert.AreEqual(BitConverter.GetBytes(0), sessionMain.MessageCount);
    }

    [Test]
    public void Test_EncryptMessage_ShouldReturnNull_WhenSharedKeysAreNotSet()
    {
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        var result = sessionMain.EncryptMessage(staticHeader, new byte[32], new byte[32]);
        Assert.IsNull(result);
    }

    [Test]
    public void Test_EncryptMessage_ShouldReturnEncryptedMessage_WhenSharedKeysAreSet()
    {
        var encryptedMessage = RandomUtility.GenerateRandomData(32);
        var decryptedMessage = RandomUtility.GenerateRandomData(32);
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var handshakePacket = new HandshakePacketBase(new byte[32], new byte[32], new byte[32], new byte[32]);

        mockSessionCrypto
            .Setup(x => x.GenerateSessionKeys(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new SharedKeys(new byte[32]));
        mockAesCrypto
            .Setup(x => x.AesGcmEncrypt(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(encryptedMessage);
        mockAesCrypto
            .Setup(x => x.AesGcmDecrypt(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(decryptedMessage);

        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);

        sessionMain.SetChallengeData(new byte[32], new byte[32]);
        sessionMain.DecryptMessageWithNewKeys(staticHeader, null, null, handshakePacket, null);

        var result = sessionMain.EncryptMessage(staticHeader, new byte[32], new byte[32]);
        Assert.IsTrue(result.SequenceEqual(encryptedMessage));
        Assert.AreEqual(BitConverter.GetBytes(1), sessionMain.MessageCount);
    }

    [Test]
    public void Test_DecryptedMessage_ShouldReturnNull_WhenSharedKeysAreNotSet()
    {
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);
        var result = sessionMain.DecryptMessage(staticHeader, new byte[32], new byte[32]);
        Assert.IsNull(result);
    }

    [Test]
    public void Test_DecryptedMessage_ShouldReturnDecryptedMessage_WhenSharedKeysAreSet()
    {
        var encryptedMessage = RandomUtility.GenerateRandomData(32);
        var decryptedMessage = RandomUtility.GenerateRandomData(32);
        var staticHeader = new StaticHeader(new byte[32], new byte[32], 0, new byte[32]);
        var handshakePacket = new HandshakePacketBase(new byte[32], new byte[32], new byte[32], new byte[32]);

        mockSessionCrypto
            .Setup(x => x.GenerateSessionKeys(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new SharedKeys(new byte[32]));
        mockAesCrypto
            .Setup(x => x.AesGcmEncrypt(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(encryptedMessage);
        mockAesCrypto
            .Setup(x => x.AesGcmDecrypt(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(decryptedMessage);

        var sessionMain = new SessionMain(mockSessionKeys.Object, mockAesCrypto.Object, mockSessionCrypto.Object, mockLoggerFactory.Object, SessionType.Initiator);

        sessionMain.SetChallengeData(new byte[32], new byte[32]);
        sessionMain.DecryptMessageWithNewKeys(staticHeader, null, null, handshakePacket, null);

        var result = sessionMain.DecryptMessage(staticHeader, new byte[32], new byte[32]);
        Assert.IsTrue(result.SequenceEqual(decryptedMessage));
        Assert.AreEqual(BitConverter.GetBytes(0), sessionMain.MessageCount);
    }



}
