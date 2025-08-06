using Lantern.Discv5.WireProtocol.Utility;
using NBitcoin.Secp256k1;

namespace Lantern.Discv5.WireProtocol.Session;

public class SessionKeys : ISessionKeys
{
    public SessionKeys(byte[] privateKey, byte[]? ephemeralPubkey = null)
    {
        PrivateKey = privateKey;
        EphemeralPrivateKey = ephemeralPubkey ?? RandomUtility.GenerateRandomData(SessionConstants.EcPrivateKeySize);
        PublicKey = CryptoContext.CreateECPrivKey(PrivateKey).CreatePubKey().ToBytes();
        EphemeralPublicKey = CryptoContext.CreateECPrivKey(EphemeralPrivateKey).CreatePubKey().ToBytes();
    }

    public byte[] PrivateKey { get; }

    public byte[] EphemeralPrivateKey { get; }

    public byte[] PublicKey { get; }

    public byte[] EphemeralPublicKey { get; }

    public Context CryptoContext { get; } = Context.Instance;
}