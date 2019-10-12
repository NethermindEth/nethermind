using System;
using Cortex.Containers;
using Cortex.Cryptography;

namespace Cortex.BeaconNode
{
    public class BlsSignatureService
    {
        public Func<BLSParameters, BLS> SignatureAlgorithFactory { get; set; } = blsParameters => BLS.Create(blsParameters);

        public bool BlsVerify(BlsPublicKey publicKey, Hash32 signingRoot, BlsSignature signature, Domain domain)
        {
            var blsParameters = new BLSParameters() { PublicKey = publicKey.AsSpan().ToArray() };
            var signatureAlgorithm = SignatureAlgorithFactory(blsParameters);

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
    }
}
