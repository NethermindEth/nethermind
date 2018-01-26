namespace Nevermind.Core.Crypto
{
    public class EciesCipher : IEciesCipher
    {
        public byte[] Decrypt(PrivateKey privateKey, byte[] cipherText)
        {
            throw new System.NotImplementedException();
        }

        public byte[] Encrypt(PrivateKey privateKey, byte[] message)
        {
            throw new System.NotImplementedException();
        }
    }
}