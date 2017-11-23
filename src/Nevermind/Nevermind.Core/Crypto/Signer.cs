using System;
using System.Diagnostics;
using Nevermind.Core.Encoding;

namespace Nevermind.Core.Crypto
{
    /// <summary>
    ///     for signer tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public static class Signer
    {
        //public static BigInteger Secp256k1n = new BigInteger(115792_08923731_61954235_70985008_68790785_28375642_79074904_38260516_31415181_61494337);

        public static void Sign(Transaction transaction, PrivateKey privateKey, bool eip155 = false, ChainId chainId = 0)
        {
            int chainIdValue = (int)chainId;

            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, eip155, chainIdValue));
            transaction.Signature = Sign(privateKey, hash);
        }

        public static bool Verify(Address sender, Transaction transaction, bool eip155 = false, ChainId chainId = 0)
        {
            int chainIdValue = (int)chainId;

            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, eip155, chainIdValue));
            Address recovered = RecoverSignerAddress(transaction.Signature, hash);
            return recovered.Equals(sender);
        }

        public static Address Recover(Transaction transaction)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true));
            return RecoverSignerAddress(transaction.Signature, hash);
        }

        public static Signature Sign(PrivateKey privateKey, byte[] bytes)
        {
            if (!Secp256k1.Proxy.Proxy.VerifyPrivateKey(privateKey.Hex))
            {
                throw new ArgumentException("Invalid private key", nameof(privateKey));
            }

            int recoveryId;
            byte[] signature = Secp256k1.Proxy.Proxy.SignCompact(bytes, privateKey.Hex, out recoveryId);

            return new Signature(signature, recoveryId);
        }

        public static Signature Sign(PrivateKey privateKey, Keccak message)
        {
            if (!Secp256k1.Proxy.Proxy.VerifyPrivateKey(privateKey.Hex))
            {
                throw new ArgumentException("Invalid private key", nameof(privateKey));
            }

            int recoveryId;
            byte[] signatureBytes = Secp256k1.Proxy.Proxy.SignCompact(message.Bytes, privateKey.Hex, out recoveryId);


            Signature signature = new Signature(signatureBytes, recoveryId);
#if DEBUG
            Address address = RecoverSignerAddress(signature, message);
            Debug.Assert(address.Equals(privateKey.Address));
#endif

            return signature;

        }

        public static Address RecoverSignerAddress(Signature signature, Keccak message)
        {
            byte[] publicKey = Secp256k1.Proxy.Proxy.RecoverKeyFromCompact(message.Bytes, signature.Bytes, signature.RecoveryId, false);
            return new PublicKey(publicKey).Address;
        }
    }
}