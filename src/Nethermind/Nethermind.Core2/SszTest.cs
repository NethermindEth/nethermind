using Nethermind.Core2.Crypto;

namespace Nethermind.Core2
{
    static public class SszTest
    {
        public static BlsPublicKey TestKey1 = new BlsPublicKey(
            "0x000102030405060708090a0b0c0d0e0f" +
            "101112131415161718191a1b1c1d1e1f" +
            "202122232425262728292a2b2c2d2e2f");

        public static BlsSignature TestSig1 = new BlsSignature(new byte[BlsSignature.Length]);
    }
}