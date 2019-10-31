using System;
using System.Security.Cryptography;
using Cortex.Containers;
using Cortex.Cryptography;

namespace Cortex.BeaconNode
{
    public class CryptographyService : ICryptographyService
    {
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();

        public Func<BLSParameters, BLS> SignatureAlgorithmFactory { get; set; } = blsParameters => BLS.Create(blsParameters);

        public bool BlsVerify(BlsPublicKey publicKey, Hash32 messageHash, BlsSignature signature, Domain domain)
        {
            //// HACK: for zero values (that fail) until Herumi supports ETH2 hash-to-point
            if (messageHash == Hash32.Zero)
            {
                return signature == new BlsSignature();
            }

            var blsParameters = new BLSParameters() { PublicKey = publicKey.AsSpan().ToArray() };
            using var signatureAlgorithm = SignatureAlgorithmFactory(blsParameters);
            return signatureAlgorithm.VerifyHash(messageHash.AsSpan(), signature.AsSpan(), domain.AsSpan().ToArray());
        }

        public Hash32 Hash(Hash32 a, Hash32 b)
        {
            var input = new Span<byte>(new byte[64]);
            a.AsSpan().CopyTo(input);
            b.AsSpan().CopyTo(input.Slice(32));
            return Hash(input);
        }

        public Hash32 Hash(ReadOnlySpan<byte> bytes)
        {
            var result = new Span<byte>(new byte[32]);
            var success = _hashAlgorithm.TryComputeHash(bytes, result, out var bytesWritten);
            if (!success || bytesWritten != 32)
            {
                throw new InvalidOperationException("Error generating hash value.");
            }
            return new Hash32(result);
        }
    }
}
