namespace Nevermind.Core.Crypto
{
    public static class Random
    {
        private static readonly System.Security.Cryptography.RandomNumberGenerator SecureRandom = new System.Security.Cryptography.RNGCryptoServiceProvider();

        public static byte[] GeneratePrivateKey()
        {
            var bytes = new byte[32];
            SecureRandom.GetBytes(bytes);
            return bytes;
        }

        public static byte[] GenerateRandomBytes(int lenght)
        {
            var bytes = new byte[lenght];
            SecureRandom.GetBytes(bytes);
            return bytes;
        }
    }
}