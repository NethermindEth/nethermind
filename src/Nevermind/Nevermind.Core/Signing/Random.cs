namespace Nevermind.Core.Signing
{
    public static class Random
    {
        private static readonly System.Security.Cryptography.RandomNumberGenerator SecureRandom = new System.Security.Cryptography.RNGCryptoServiceProvider();

        public static byte[] GeneratePrivateKey()
        {
            byte[] bytes = new byte[32];
            SecureRandom.GetBytes(bytes);
            return bytes;
        }
    }
}