using Org.BouncyCastle.Security;

namespace Nevermind.Core
{
    public static class Random
    {
        private static readonly SecureRandom SecureRandom = new SecureRandom();

        public static byte[] GeneratePrivateKey()
        {
            byte[] bytes = new byte[32];
            SecureRandom.NextBytes(bytes);
            return bytes;
        }
    }
}