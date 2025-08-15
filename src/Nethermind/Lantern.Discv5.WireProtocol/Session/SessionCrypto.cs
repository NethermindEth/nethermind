using System.Security.Cryptography;
using System.Text;
using Lantern.Discv5.Rlp;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace Lantern.Discv5.WireProtocol.Session;

public class SessionCrypto : ISessionCrypto
{
    public SharedKeys GenerateSessionKeys(byte[] sharedSecret, byte[] nodeIdA, byte[] nodeIdB, byte[] challengeData)
    {
        var kdfInfo = ByteArrayUtils.Concatenate(Encoding.UTF8.GetBytes(SessionConstants.DiscoveryAgreement), nodeIdA, nodeIdB);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, challengeData);
        var keyData = new byte[SessionConstants.EcPrivateKeySize];

        HKDF.Expand(HashAlgorithmName.SHA256, prk, keyData, kdfInfo);

        return new SharedKeys(keyData);
    }

    public byte[] GenerateIdSignature(ISessionKeys sessionKeys, byte[] challengeData, byte[] ephemeralPubkey, byte[] nodeId)
    {
        var serialisedPrivateKey = new Context().CreateECPrivKey(sessionKeys.PrivateKey);
        var idSignatureText = Encoding.UTF8.GetBytes(SessionConstants.IdSignatureProof);
        var idSignatureInput = ByteArrayUtils.Concatenate(idSignatureText, challengeData, ephemeralPubkey, nodeId);

        var hash = SHA256.HashData(idSignatureInput);

        serialisedPrivateKey.TrySignECDSA(hash, out var signature);

        return ConcatenateSignature(signature!);
    }

    public bool VerifyIdSignature(byte[] idSignature, byte[] challengeData, byte[] publicKey, byte[] ephPubKey, byte[] selfNodeId, Context cryptoContext)
    {
        var idSignatureText = Encoding.UTF8.GetBytes(SessionConstants.IdSignatureProof);
        var idSignatureInput = ByteArrayUtils.Concatenate(idSignatureText, challengeData, ephPubKey, selfNodeId);
        var hash = SHA256.HashData(idSignatureInput);
        var key = cryptoContext.CreatePubKey(publicKey);

        return SecpECDSASignature.TryCreateFromCompact(idSignature, out var signature) && key.SigVerify(signature, hash);
    }

    public byte[] GenerateSharedSecret(byte[] privateKey, byte[] publicKey, Context cryptoContext)
    {
        var serialisedPrivateKey = cryptoContext.CreateECPrivKey(privateKey);
        var serialisedPublicKey = cryptoContext.CreatePubKey(publicKey);
        var sharedSecret = serialisedPublicKey.GetSharedPubkey(serialisedPrivateKey).ToBytes();
        return sharedSecret;
    }

    private static byte[] ConcatenateSignature(SecpECDSASignature signature)
    {
        var rBytes = signature.r.ToBytes();
        var sBytes = signature.s.ToBytes();
        var result = new byte[rBytes.Length + sBytes.Length];

        Buffer.BlockCopy(rBytes, 0, result, 0, rBytes.Length);
        Buffer.BlockCopy(sBytes, 0, result, rBytes.Length, sBytes.Length);

        return result;
    }
}