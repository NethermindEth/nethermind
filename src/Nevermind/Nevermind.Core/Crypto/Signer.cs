using System;
using System.Diagnostics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Potocol;

namespace Nevermind.Core.Crypto
{
    public interface ISigner
    {
        Signature Sign(PrivateKey privateKey, byte[] bytes);
        Signature Sign(PrivateKey privateKey, Keccak message);
        void Sign(PrivateKey privateKey, Transaction transaction);
        Address Recover(Signature signature, Keccak message);
        bool Verify(Address sender, Transaction transaction);
        Address Recover(Transaction transaction);
    }

    /// <summary>
    ///     for signer tests
    ///     http://blog.enuma.io/update/2016/11/01/a-tale-of-two-curves-hardware-signing-for-ethereum.html
    /// </summary>
    public class Signer : ISigner
    {
        private readonly IProtocolSpecification _protocolSpecification;

        private readonly int _chainIdValue;
        //public static BigInteger Secp256k1n = new BigInteger(115792_08923731_61954235_70985008_68790785_28375642_79074904_38260516_31415181_61494337);

        public Signer(IProtocolSpecification protocolSpecification, int chainIdValue)
        {
            _protocolSpecification = protocolSpecification;
            _chainIdValue = chainIdValue;
        }

        public Signer(IProtocolSpecification protocolSpecification, ChainId chainId)
            : this(protocolSpecification, (int)chainId)
        {
        }

        public void Sign(PrivateKey privateKey, Transaction transaction)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, _protocolSpecification.IsEip155Enabled, _chainIdValue));
            transaction.Signature = Sign(privateKey, hash);
        }

        public bool Verify(Address sender, Transaction transaction)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true, _protocolSpecification.IsEip155Enabled, _chainIdValue));
            Address recovered = Recover(transaction.Signature, hash);
            return recovered.Equals(sender);
        }

        public Address Recover(Transaction transaction)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(transaction, true));
            return Recover(transaction.Signature, hash);
        }

        public Signature Sign(PrivateKey privateKey, byte[] bytes)
        {
            if (!Secp256k1.Proxy.Proxy.VerifyPrivateKey(privateKey.Hex))
            {
                throw new ArgumentException("Invalid private key", nameof(privateKey));
            }

            int recoveryId;
            byte[] signature = Secp256k1.Proxy.Proxy.SignCompact(bytes, privateKey.Hex, out recoveryId);

            return new Signature(signature, recoveryId);
        }

        public Signature Sign(PrivateKey privateKey, Keccak message)
        {
            if (!Secp256k1.Proxy.Proxy.VerifyPrivateKey(privateKey.Hex))
            {
                throw new ArgumentException("Invalid private key", nameof(privateKey));
            }

            int recoveryId;
            byte[] signatureBytes = Secp256k1.Proxy.Proxy.SignCompact(message.Bytes, privateKey.Hex, out recoveryId);


            Signature signature = new Signature(signatureBytes, recoveryId);
#if DEBUG
            Address address = Recover(signature, message);
            Debug.Assert(address.Equals(privateKey.Address));
#endif

            return signature;
        }

        public Address Recover(Signature signature, Keccak message)
        {
            byte[] publicKey = Secp256k1.Proxy.Proxy.RecoverKeyFromCompact(message.Bytes, signature.Bytes, signature.RecoveryId, false);
            return new PublicKey(publicKey).Address;
        }
    }
}