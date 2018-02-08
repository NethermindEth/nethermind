using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Network.Test
{
    public static class NetTestVectors
    {
        public static readonly PrivateKey StaticKeyA = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        public static readonly PrivateKey StaticKeyB = new PrivateKey("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        public static readonly PrivateKey EphemeralKeyA = new PrivateKey("869d6ecf5211f1cc60418a13b9d870b22959d0c16f02bec714c960dd2298a32d");
        public static readonly PublicKey EphemeralPublicKeyA = new PublicKey(new Hex("654d1044b69c577a44e5f01a1209523adb4026e70c62d1c13a067acabc09d2667a49821a0ad4b634554d330a15a58fe61f8a8e0544b310c6de7b0c8da7528a8d"));
        public static readonly PrivateKey EphemeralKeyB = new PrivateKey("e238eb8e04fee6511ab04c6dd3c89ce097b11f25d584863ac2b6d5b35b1847e4");
        public static readonly PublicKey EphemeralPublicKeyB = new PublicKey(new Hex("b6d82fa3409da933dbf9cb0140c5dde89f4e64aec88d476af648880f4a10e1e49fe35ef3e69e93dd300b4797765a747c6384a6ecf5db9c2690398607a86181e4"));
        public static readonly byte[] NonceA = new Hex("7e968bba13b6c50e2c4cd7f241cc0d64d1ac25c7f5952df231ac6a2bda8ee5d6");
        public static readonly byte[] NonceB = new Hex("559aead08264d5795d3909718cdd05abd49572e84fe55590eef31a88a08fdffd");

        public static readonly byte[] AesSecret = new Hex("80e8632c05fed6fc2a13b0f8d31a3cf645366239170ea067065aba8e28bac487");
        public static readonly byte[] MacSecret = new Hex("2ea74ec5dae199227dff1af715362700e989d889d7a493cb0639691efb8e5f98");
        
        public static readonly byte[] BIngressMacFoo = new Hex("0c7ec6340062cc46f5e9f1e3cf86f8c8c403c5a0964f5df0ebd34a75ddc86db5");
    }
}