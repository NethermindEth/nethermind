using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    // TODO: refactor the API here after everything is cleared
    public interface IEciesCipher
    {
        byte[] Decrypt(PrivateKey privateKeyParameters, byte[] ciphertextBody, byte[] macData = null);
        byte[] Encrypt(PublicKey recipientPublicKey, byte[] plaintext, byte[] macData);
    }
}