namespace Nevermind.Core.Crypto
{
    public interface IEciesCipher
    {
        byte[] Decrypt(PrivateKey privateKey, byte[] cipherText);
        byte[] Encrypt(PrivateKey privateKey, byte[] message);
    }
}