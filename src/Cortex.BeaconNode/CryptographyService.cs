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

        public bool BlsVerify(BlsPublicKey publicKey, Hash32 signingRoot, BlsSignature signature, Domain domain)
        {
            var blsParameters = new BLSParameters() { PublicKey = publicKey.AsSpan().ToArray() };
            var signatureAlgorithm = SignatureAlgorithmFactory(blsParameters);

            var hash = new Span<byte>(new byte[Hash32.Length + Domain.Length]);
            signingRoot.AsSpan().CopyTo(hash);
            domain.AsSpan().CopyTo(hash.Slice(Hash32.Length));

            //var g2 = HashToG2(signingRoot, domain);

            // NOTE: Might need to calculate our own G2 and then call blsVerifyPairing

            return signatureAlgorithm.VerifyHash(hash.ToArray(), signature);
        }

        public ReadOnlySpan<byte> HashToG2(Hash32 signingRoot, Domain domain)
        {

            throw new NotImplementedException();
        }

        public Hash32 Hash(Hash32 a, Hash32 b)
        {
            var input = new Span<byte>(new byte[64]);
            a.AsSpan().CopyTo(input);
            b.AsSpan().CopyTo(input.Slice(32));
            var result = new Span<byte>(new byte[32]);
            var success = _hashAlgorithm.TryComputeHash(input, result, out var bytesWritten);
            if (!success || bytesWritten != 32)
            {
                throw new InvalidOperationException("Error generating hash value.");
            }
            return result;
        }
    }
}
