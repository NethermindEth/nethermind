using System.Security.Cryptography;

namespace Cortex.SimpleSerialize.Tests
{
    public static class HashUtility
    {
        private static readonly HashAlgorithm hash = SHA256.Create();

        public static byte[] Hash(byte[] c1, byte[] c2)
        {
            var b = new byte[64];
            c1.CopyTo(b, 0);
            c2.CopyTo(b, 32);
            return hash.ComputeHash(b);
        }
    }
}
