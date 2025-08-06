using NBitcoin.Secp256k1;

namespace Lantern.Discv5.WireProtocol.Session;

public interface ISessionCrypto
{
    SharedKeys GenerateSessionKeys(byte[] sharedSecret, byte[] nodeIdA, byte[] nodeIdB,
        byte[] challengeData);

    byte[] GenerateIdSignature(ISessionKeys sessionKeys, byte[] challengeData, byte[] ephemeralPubkey,
        byte[] nodeId);

    bool VerifyIdSignature(byte[] idSignature, byte[] challengeData, byte[] publicKey, byte[] ephPubKey,
        byte[] selfNodeId, Context cryptoContext);

    byte[] GenerateSharedSecret(byte[] privateKey, byte[] publicKey, Context cryptoContext);
}