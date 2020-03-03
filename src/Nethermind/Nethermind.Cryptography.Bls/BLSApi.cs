using System;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.Cryptography
{
    /// <summary>
    /// BLS signature scheme API as defined in specifications. 
    /// Note that it does not suppor the additional domain parameter for hash-to-point used in some implementation (e.g. Ethereum 2.0).
    /// </summary>
    public class BLSApi
    {
        private readonly BLS _bls;

        public BLSApi(BLS bls)
        {
            _bls = bls;
        }

        public byte[] Aggregate(IEnumerable<byte[]> signatures)
        {
            var signatureLength = signatures.First().Length;
            var signaturesSpan = new Span<byte>(new byte[signatures.Count() * signatureLength]);
            var index = 0;
            foreach (var signature in signatures)
            {
                signature.CopyTo(signaturesSpan.Slice(index * signatureLength));
            }
            var aggregate = new Span<byte>(new byte[signatureLength]);
            var success = _bls.TryAggregateSignatures(signaturesSpan, aggregate, out var bytesWritten);
            return aggregate.ToArray();
        }

        public bool AggregateVerify(IEnumerable<Tuple<byte, byte>> publicKeysAndMessages, ReadOnlySpan<byte> signature)
        {
            // Aggregate verify is across multiple public keys...
            // (.NET crypto AsymmetricAlgorithm usually represents one key only)

            throw new NotImplementedException();
        }

        public (byte[] publicKey, byte[] secretKey) KeyGen(ReadOnlySpan<byte> inputKeyMaterial)
        {
            var parameters = new BLSParameters()
            {
                InputKeyMaterial = inputKeyMaterial.ToArray()
            };
            _bls.ImportParameters(parameters);

            throw new NotImplementedException();
            //return (new byte[0], new byte[0]);
        }

        public byte[] Sign(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> message)
        {
            var parameters = new BLSParameters()
            {
                PrivateKey = secretKey.ToArray()
            };
            _bls.ImportParameters(parameters);
            var hash = new Span<byte>(new byte[32]);
            var hashSuccess = _bls.HashAlgorithm.TryComputeHash(message, hash, out var hashBytesWritten);
            var signature = new Span<byte>(new byte[96]);
            var signSuccess = _bls.TrySignHash(hash, signature, out var signBytesWritten);
            return signature.ToArray();
        }

        public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
        {
            throw new NotImplementedException();
        }

        // PopProve(SK)
        // PopVerify(PK, proof)
        // FastAggregateVerify(PK1...PKn, message, signature)
    }
}
