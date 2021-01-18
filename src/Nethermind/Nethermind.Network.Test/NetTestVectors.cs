//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Network.Test
{
    public static class NetTestVectors
    {
        public static EncryptionSecrets BuildSecretsWithSameIngressAndEgress()
        {
            EncryptionSecrets secrets = new EncryptionSecrets();
            secrets.AesSecret = AesSecret;
            secrets.MacSecret = MacSecret;

            byte[] bytes = AesSecret.Xor(MacSecret);

            KeccakDigest egressMac = new KeccakDigest(256);
            egressMac.BlockUpdate(bytes, 0, 32);
            secrets.EgressMac = egressMac;

            KeccakDigest ingressMac = new KeccakDigest(256);
            ingressMac.BlockUpdate(bytes, 0, 32);
            secrets.IngressMac = ingressMac;
            return secrets;
        }

        public static (EncryptionSecrets A, EncryptionSecrets B) GetSecretsPair()
        {
            EncryptionHandshake handshakeA = new EncryptionHandshake();
            handshakeA.InitiatorNonce = TestItem.KeccakA.Bytes;
            handshakeA.RecipientNonce = TestItem.KeccakB.Bytes;
            handshakeA.EphemeralPrivateKey = TestItem.PrivateKeyA;
            handshakeA.RemoteEphemeralPublicKey = TestItem.PrivateKeyB.PublicKey;
            handshakeA.AckPacket = new Packet(new byte[128]);
            handshakeA.AuthPacket = new Packet(new byte[128]);

            EncryptionHandshake handshakeB = new EncryptionHandshake();
            handshakeB.InitiatorNonce = TestItem.KeccakA.Bytes;
            handshakeB.RecipientNonce = TestItem.KeccakB.Bytes;
            handshakeB.EphemeralPrivateKey = TestItem.PrivateKeyB;
            handshakeB.RemoteEphemeralPublicKey = TestItem.PrivateKeyA.PublicKey;
            handshakeB.AckPacket = new Packet(new byte[128]);
            handshakeB.AuthPacket = new Packet(new byte[128]);

            HandshakeService.SetSecrets(handshakeA, HandshakeRole.Initiator);
            HandshakeService.SetSecrets(handshakeB, HandshakeRole.Recipient);

            Assert.AreEqual(handshakeA.Secrets.AesSecret, handshakeB.Secrets.AesSecret, "aes");
            Assert.AreEqual(handshakeA.Secrets.MacSecret, handshakeB.Secrets.MacSecret, "mac");

            KeccakDigest aIngress = handshakeA.Secrets.IngressMac.Copy() as KeccakDigest;
            KeccakDigest bIngress = handshakeB.Secrets.IngressMac.Copy() as KeccakDigest;
            KeccakDigest aEgress = handshakeA.Secrets.EgressMac.Copy() as KeccakDigest;
            KeccakDigest bEgress = handshakeB.Secrets.EgressMac.Copy() as KeccakDigest;

            byte[] aIngressFinal = new byte[32];
            byte[] bIngressFinal = new byte[32];
            byte[] aEgressFinal = new byte[32];
            byte[] bEgressFinal = new byte[32];

            aIngress.DoFinal(aIngressFinal, 0);
            bIngress.DoFinal(bIngressFinal, 0);
            aEgress.DoFinal(aEgressFinal, 0);
            bEgress.DoFinal(bEgressFinal, 0);

            Assert.AreEqual(aIngressFinal.ToHexString(), bEgressFinal.ToHexString());
            Assert.AreEqual(aEgressFinal.ToHexString(), bIngressFinal.ToHexString());

            return (handshakeA.Secrets, handshakeB.Secrets);
        }

        public static readonly PrivateKey StaticKeyA = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        public static readonly PrivateKey StaticKeyB = new PrivateKey("b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291");
        public static readonly PrivateKey EphemeralKeyA = new PrivateKey("869d6ecf5211f1cc60418a13b9d870b22959d0c16f02bec714c960dd2298a32d");
        public static readonly PublicKey EphemeralPublicKeyA = new PublicKey("654d1044b69c577a44e5f01a1209523adb4026e70c62d1c13a067acabc09d2667a49821a0ad4b634554d330a15a58fe61f8a8e0544b310c6de7b0c8da7528a8d");
        public static readonly PrivateKey EphemeralKeyB = new PrivateKey("e238eb8e04fee6511ab04c6dd3c89ce097b11f25d584863ac2b6d5b35b1847e4");
        public static readonly PublicKey EphemeralPublicKeyB = new PublicKey("b6d82fa3409da933dbf9cb0140c5dde89f4e64aec88d476af648880f4a10e1e49fe35ef3e69e93dd300b4797765a747c6384a6ecf5db9c2690398607a86181e4");
        public static readonly byte[] NonceA = Bytes.FromHexString("7e968bba13b6c50e2c4cd7f241cc0d64d1ac25c7f5952df231ac6a2bda8ee5d6");
        public static readonly byte[] NonceB = Bytes.FromHexString("559aead08264d5795d3909718cdd05abd49572e84fe55590eef31a88a08fdffd");

        public static readonly byte[] AesSecret = Bytes.FromHexString("80e8632c05fed6fc2a13b0f8d31a3cf645366239170ea067065aba8e28bac487");
        public static readonly byte[] MacSecret = Bytes.FromHexString("2ea74ec5dae199227dff1af715362700e989d889d7a493cb0639691efb8e5f98");

        public static readonly byte[] BIngressMacFoo = Bytes.FromHexString("0c7ec6340062cc46f5e9f1e3cf86f8c8c403c5a0964f5df0ebd34a75ddc86db5");

        public static readonly byte[] AuthEip8 = Bytes.FromHexString("01b304ab7578555167be8154d5cc456f567d5ba302662433674222360f08d5f1534499d3678b513b" +
                                                                     "0fca474f3a514b18e75683032eb63fccb16c156dc6eb2c0b1593f0d84ac74f6e475f1b8d56116b84" +
                                                                     "9634a8c458705bf83a626ea0384d4d7341aae591fae42ce6bd5c850bfe0b999a694a49bbbaf3ef6c" +
                                                                     "da61110601d3b4c02ab6c30437257a6e0117792631a4b47c1d52fc0f8f89caadeb7d02770bf999cc" +
                                                                     "147d2df3b62e1ffb2c9d8c125a3984865356266bca11ce7d3a688663a51d82defaa8aad69da39ab6" +
                                                                     "d5470e81ec5f2a7a47fb865ff7cca21516f9299a07b1bc63ba56c7a1a892112841ca44b6e0034dee" +
                                                                     "70c9adabc15d76a54f443593fafdc3b27af8059703f88928e199cb122362a4b35f62386da7caad09" +
                                                                     "c001edaeb5f8a06d2b26fb6cb93c52a9fca51853b68193916982358fe1e5369e249875bb8d0d0ec3" +
                                                                     "6f917bc5e1eafd5896d46bd61ff23f1a863a8a8dcd54c7b109b771c8e61ec9c8908c733c0263440e" +
                                                                     "2aa067241aaa433f0bb053c7b31a838504b148f570c0ad62837129e547678c5190341e4f1693956c" +
                                                                     "3bf7678318e2d5b5340c9e488eefea198576344afbdf66db5f51204a6961a63ce072c8926c");

        public static readonly byte[] AckEip8 = Bytes.FromHexString("01ea0451958701280a56482929d3b0757da8f7fbe5286784beead59d95089c217c9b917788989470" +
                                                                    "b0e330cc6e4fb383c0340ed85fab836ec9fb8a49672712aeabbdfd1e837c1ff4cace34311cd7f4de" +
                                                                    "05d59279e3524ab26ef753a0095637ac88f2b499b9914b5f64e143eae548a1066e14cd2f4bd7f814" +
                                                                    "c4652f11b254f8a2d0191e2f5546fae6055694aed14d906df79ad3b407d94692694e259191cde171" +
                                                                    "ad542fc588fa2b7333313d82a9f887332f1dfc36cea03f831cb9a23fea05b33deb999e85489e645f" +
                                                                    "6aab1872475d488d7bd6c7c120caf28dbfc5d6833888155ed69d34dbdc39c1f299be1057810f34fb" +
                                                                    "e754d021bfca14dc989753d61c413d261934e1a9c67ee060a25eefb54e81a4d14baff922180c395d" +
                                                                    "3f998d70f46f6b58306f969627ae364497e73fc27f6d17ae45a413d322cb8814276be6ddd13b885b" +
                                                                    "201b943213656cde498fa0e9ddc8e0b8f8a53824fbd82254f3e2c17e8eaea009c38b4aa0a3f306e8" +
                                                                    "797db43c25d68e86f262e564086f59a2fc60511c42abfb3057c247a8a8fe4fb3ccbadde17514b7ac" +
                                                                    "8000cdb6a912778426260c47f38919a91f25f4b5ffb455d6aaaf150f7e5529c100ce62d6d92826a7" +
                                                                    "1778d809bdf60232ae21ce8a437eca8223f45ac37f6487452ce626f549b3b5fdee26afd2072e4bc7" +
                                                                    "5833c2464c805246155289f4");
    }
}
