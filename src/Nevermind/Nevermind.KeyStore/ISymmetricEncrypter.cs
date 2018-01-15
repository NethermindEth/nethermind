namespace Nevermind.KeyStore
{
    public interface ISymmetricEncrypter
    {
        byte[] Encrypt(byte[] content, byte[] key, byte[] iv);
        byte[] Decrypt(byte[] cipher, byte[] key, byte[] iv);
    }
}