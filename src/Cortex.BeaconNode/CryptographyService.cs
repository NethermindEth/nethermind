using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Cortex.Containers;
using Cortex.Cryptography;

namespace Cortex.BeaconNode
{
    public class CryptographyService : ICryptographyService
    {
        private const int HashLength = 32;
        private const int PublicKeyLength = 48;
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();

        public Func<BLSParameters, BLS> SignatureAlgorithmFactory { get; set; } = blsParameters => BLS.Create(blsParameters);

        public BlsPublicKey BlsAggregatePublicKeys(IEnumerable<BlsPublicKey> publicKeys)
        {
            var publicKeysSpan = new Span<byte>(new byte[publicKeys.Count() * PublicKeyLength]);
            var publicKeysSpanIndex = 0;
            foreach (var publicKey in publicKeys)
            {
                publicKey.AsSpan().CopyTo(publicKeysSpan.Slice(publicKeysSpanIndex));
                publicKeysSpanIndex += PublicKeyLength;
            }
            using var signatureAlgorithm = SignatureAlgorithmFactory(new BLSParameters());
            var aggregatePublicKey = new Span<byte>(new byte[PublicKeyLength]);
            var success = signatureAlgorithm.TryAggregatePublicKeys(publicKeysSpan, aggregatePublicKey, out var bytesWritten);
            if (!success || bytesWritten != PublicKeyLength)
            {
                throw new Exception("Error generating aggregate public key.");
            }
            return new BlsPublicKey(aggregatePublicKey);
        }

        public bool BlsVerify(BlsPublicKey publicKey, Hash32 messageHash, BlsSignature signature, Domain domain)
        {
            var blsParameters = new BLSParameters() { PublicKey = publicKey.AsSpan().ToArray() };
            using var signatureAlgorithm = SignatureAlgorithmFactory(blsParameters);
            return signatureAlgorithm.VerifyHash(messageHash.AsSpan(), signature.AsSpan(), domain.AsSpan());
        }

        public bool BlsVerifyMultiple(IEnumerable<BlsPublicKey> publicKeys, IEnumerable<Hash32> messageHashes, BlsSignature signature, Domain domain)
        {
            var publicKeysSpan = new Span<byte>(new byte[publicKeys.Count() * PublicKeyLength]);
            var publicKeysSpanIndex = 0;
            foreach (var publicKey in publicKeys)
            {
                publicKey.AsSpan().CopyTo(publicKeysSpan.Slice(publicKeysSpanIndex));
                publicKeysSpanIndex += PublicKeyLength;
            }

            var messageHashesSpan = new Span<byte>(new byte[publicKeys.Count() * PublicKeyLength]);
            var messageHashesSpanIndex = 0;
            foreach (var messageHash in messageHashes)
            {
                messageHash.AsSpan().CopyTo(messageHashesSpan.Slice(messageHashesSpanIndex));
                messageHashesSpanIndex += HashLength;
            }

            using var signatureAlgorithm = SignatureAlgorithmFactory(new BLSParameters());
            return signatureAlgorithm.VerifyAggregate(publicKeysSpan, messageHashesSpan, signature.AsSpan(), domain.AsSpan());
        }

        public Hash32 Hash(Hash32 a, Hash32 b)
        {
            var input = new Span<byte>(new byte[HashLength * 2]);
            a.AsSpan().CopyTo(input);
            b.AsSpan().CopyTo(input.Slice(HashLength));
            return Hash(input);
        }

        public Hash32 Hash(ReadOnlySpan<byte> bytes)
        {
            var result = new Span<byte>(new byte[HashLength]);
            var success = _hashAlgorithm.TryComputeHash(bytes, result, out var bytesWritten);
            if (!success || bytesWritten != HashLength)
            {
                throw new Exception("Error generating hash value.");
            }
            return new Hash32(result);
        }
    }
}
