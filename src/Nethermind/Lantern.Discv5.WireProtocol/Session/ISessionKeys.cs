using NBitcoin.Secp256k1;

namespace Lantern.Discv5.WireProtocol.Session;

public interface ISessionKeys
{
    byte[] PrivateKey { get; }

    byte[] EphemeralPrivateKey { get; }

    byte[] PublicKey { get; }

    byte[] EphemeralPublicKey { get; }

    Context CryptoContext { get; }
}