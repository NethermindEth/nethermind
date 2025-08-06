using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Session;

public class SessionMain(ISessionKeys sessionKeys, IAesCrypto aesCrypto, ISessionCrypto sessionCrypto,
        ILoggerFactory loggerFactory, SessionType sessionType)
    : ISessionMain
{
    private readonly ILogger<ISessionMain> _logger = loggerFactory.CreateLogger<ISessionMain>();
    private byte[]? _challengeData;
    private SharedKeys? _currentSharedKeys;
    private int _messageCount;

    public bool IsEstablished { get; private set; }

    public byte[] MessageCount => BitConverter.GetBytes(_messageCount);

    public byte[] PublicKey => sessionKeys.PublicKey;

    public byte[] EphemeralPublicKey => sessionKeys.EphemeralPublicKey;

    public void SetChallengeData(byte[] maskingIv, byte[] header)
    {
        _challengeData = ByteArrayUtils.JoinByteArrays(maskingIv, header);
    }

    public byte[]? GenerateIdSignature(byte[] destNodeId)
    {
        if (_challengeData == null)
        {
            _logger.LogError("Challenge data is not set. Cannot generate id signature");
            return null;
        }

        return sessionCrypto.GenerateIdSignature(sessionKeys, _challengeData, EphemeralPublicKey, destNodeId);
    }

    public bool VerifyIdSignature(HandshakePacketBase handshakePacket, byte[] publicKey, byte[] selfNodeId)
    {
        if (_challengeData == null)
        {
            _logger.LogError("Challenge data is not set. Cannot verify id signature");
            return false;
        }

        return sessionCrypto.VerifyIdSignature(handshakePacket.IdSignature, _challengeData, publicKey, handshakePacket.EphPubkey, selfNodeId, sessionKeys.CryptoContext);
    }

    public byte[]? EncryptMessageWithNewKeys(IEnr dest, StaticHeader header, byte[] selfNodeId, byte[] message, byte[] maskingIv)
    {
        var publicKey = dest.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
        var destNodeId = new IdentityVerifierV4().GetNodeIdFromRecord(dest);
        var sharedSecret = sessionCrypto.GenerateSharedSecret(sessionKeys.EphemeralPrivateKey, publicKey, sessionKeys.CryptoContext);
        var messageAd = ByteArrayUtils.JoinByteArrays(maskingIv, header.GetHeader());

        if (_challengeData == null)
        {
            _logger.LogError("Challenge data is not set. Cannot encrypt message");
            return null;
        }

        _currentSharedKeys = sessionCrypto.GenerateSessionKeys(sharedSecret, selfNodeId, destNodeId, _challengeData);
        _messageCount++;

        _logger.LogDebug("Encrypting message with new keys");
        return aesCrypto.AesGcmEncrypt(_currentSharedKeys.InitiatorKey, header.Nonce, message, messageAd);
    }

    public byte[]? DecryptMessageWithNewKeys(StaticHeader header, byte[] maskingIv, byte[] encryptedMessage, HandshakePacketBase handshakePacket, byte[] selfNodeId)
    {
        var sharedSecret = sessionCrypto.GenerateSharedSecret(sessionKeys.PrivateKey, handshakePacket.EphPubkey, sessionKeys.CryptoContext);

        if (handshakePacket.SrcId == null)
        {
            _logger.LogError("Handshake packet does not contain a source node id. Cannot decrypt packet");
            return null;
        }

        if (_challengeData == null)
        {
            _logger.LogError("Challenge data is not set. Cannot decrypt message");
            return null;
        }

        var sharedKeys = sessionCrypto.GenerateSessionKeys(sharedSecret, handshakePacket.SrcId, selfNodeId, _challengeData);
        var messageAd = ByteArrayUtils.JoinByteArrays(maskingIv, header.GetHeader());

        _logger.LogDebug("Decrypting message with new keys");
        var decryptedResult = aesCrypto.AesGcmDecrypt(sharedKeys.InitiatorKey, header.Nonce, encryptedMessage, messageAd);

        if (decryptedResult != null)
        {
            _currentSharedKeys = sharedKeys;
        }

        return decryptedResult;
    }

    public byte[]? EncryptMessage(StaticHeader header, byte[] maskingIv, byte[] rawMessage)
    {
        if (_currentSharedKeys == null)
        {
            _logger.LogError("Session keys are not available. Cannot encrypt message");
            return null;
        }

        var encryptionKey = sessionType == SessionType.Initiator ? _currentSharedKeys.InitiatorKey : _currentSharedKeys.RecipientKey;
        var messageAd = ByteArrayUtils.JoinByteArrays(maskingIv, header.GetHeader());

        _logger.LogDebug("Encrypting message");
        var encryptedMessage = aesCrypto.AesGcmEncrypt(encryptionKey, header.Nonce, rawMessage, messageAd);

        _messageCount++;
        return encryptedMessage;
    }

    public byte[]? DecryptMessage(StaticHeader header, byte[] maskingIv, byte[] encryptedMessage)
    {
        if (_currentSharedKeys == null)
        {
            _logger.LogError("Session keys are not available. Cannot decrypt message");
            return null;
        }

        var messageAd = ByteArrayUtils.JoinByteArrays(maskingIv, header.GetHeader());
        var decryptionKey = sessionType == SessionType.Initiator ? _currentSharedKeys.RecipientKey : _currentSharedKeys.InitiatorKey;

        _logger.LogDebug("Decrypting message");
        var decryptedMessage = aesCrypto.AesGcmDecrypt(decryptionKey, header.Nonce, encryptedMessage, messageAd);

        if (!IsEstablished && decryptedMessage != null)
        {
            IsEstablished = true;
            _logger.LogDebug("Session established");
        }

        return decryptedMessage;
    }
}