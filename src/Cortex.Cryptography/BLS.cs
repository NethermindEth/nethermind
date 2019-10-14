using System;
using System.Security.Cryptography;

namespace Cortex.Cryptography
{
    public abstract class BLS : AsymmetricAlgorithm
    {
        // Draft standard: https://github.com/cfrg/draft-irtf-cfrg-bls-signature
        // Want minimal-pubkey-size, with public keys points in G1, signatures points in G2
        // G1 is 384-bit integer (48 bytes)
        // G2 is pair of 384-bit integers (96 bytes)
        // Private key is < r, which is ~256 bits (32 bytes)

        public override KeySizes[] LegalKeySizes
            => new[] { new KeySizes(32 * 8, 32 * 8, 0) };

        //public override string SignatureAlgorithm => "BLS_SIG_BLS12381G2-SHA256-_NUL_";
        public override string SignatureAlgorithm => "BLS-12-381";

        public static BLS Create(BLSParameters parameters)
        {
            return new BLSHerumi(parameters);
        }

        public abstract bool TryExportBLSPrivateKey(Span<byte> desination, out int bytesWritten);

        public abstract bool TryExportBLSPublicKey(Span<byte> desination, out int bytesWritten);

        public abstract bool TrySignHash(ReadOnlySpan<byte> hash, Span<byte> destination, out int bytesWritten);

        public abstract bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature);
    }
}
