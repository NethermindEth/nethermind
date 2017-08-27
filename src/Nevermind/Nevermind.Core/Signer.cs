using System;

namespace Nevermind.Core
{
    /// <summary>
    ///     for signer tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public static class Signer
    {
        public static void Sign(Transaction transaction, PrivateKey privateKey)
        {
            var hash = Keccak.Compute(transaction.Collapse());
            throw new NotImplementedException();
        }

        public static Address Recover(Transaction transaction)
        {
            var publicKey = new PublicKey(new byte[64]);
            return publicKey.Address;
        }

        public static Signature Sign(PrivateKey privateKey, byte[] message)
        {
            if (!Secp256k1.Proxy.Proxy.VerifyPrivateKey(privateKey.Bytes))
            {
                throw new NotImplementedException();
            }

            int recoveryId;
            byte[] signature = Secp256k1.Proxy.Proxy.SignCompact(message, privateKey.Bytes, out recoveryId);

            byte[] publicKey = Secp256k1.Proxy.Proxy.RecoverKeyFromCompact(message, signature, recoveryId, false);
            string hashRecovered = new PublicKey(publicKey).ToString();

            string hash = privateKey.PublicKey.ToString();
            if (hash != hashRecovered)
            {
                throw new NotImplementedException($"{hash} {hashRecovered}");
            }

            return new Signature(signature, recoveryId);
        }
        
        public static Address RecoverSignerAddress(Signature signature, byte[] message)
        {
            byte[] publicKey = Secp256k1.Proxy.Proxy.RecoverKeyFromCompact(message, signature.Bytes, signature.RecoveryId, false);
            return new PublicKey(publicKey).Address;
        }
    }
}