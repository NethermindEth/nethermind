using System;
using System.Security.Cryptography;

namespace Cortex.Cryptography
{
    public abstract class BLS : AsymmetricAlgorithm
    {
        public override KeySizes[] LegalKeySizes
            => new[] { new KeySizes(48 * 8, 96 * 8, 48 * 8) };

        //public override string SignatureAlgorithm => "BLS_SIG_BLS12381G2-SHA256-_NUL_";
        public override string SignatureAlgorithm => "BLS-12-381";

        public static BLS Create(BLSParameters parameters)
        {
            return new BLSHerumi(parameters);
        }

        public abstract bool TrySignHash(ReadOnlySpan<byte> hash, Span<byte> destination, out int bytesWritten);

        public abstract bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature);
    }
}
