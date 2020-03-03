using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Cortex.Cryptography
{
    /// <summary>
    /// Boneh–Lynn–Shacham (BLS) signature scheme, draft standard https://github.com/cfrg/draft-irtf-cfrg-bls-signature
    /// </summary>
    public abstract class BLS : AsymmetricAlgorithm
    {
        private HashAlgorithm _hashAlgorithm = SHA256.Create();

        protected BLS()
        {
            // NOTES

            // Draft standard: https://github.com/cfrg/draft-irtf-cfrg-bls-signature
            // Want minimal-pubkey-size, with public keys points in G1, signatures points in G2
            // G1 is 384-bit integer (48 bytes)
            // G2 is pair of 384-bit integers (96 bytes)
            // Private key is < r, which is ~256 bits (32 bytes)

            // Also
            // https://datatracker.ietf.org/doc/draft-irtf-cfrg-bls-signature

            // Example algorithm name "BLS_SIG_BLS12381G2-SHA256-_NUL_";

            // BLS_SIG_<h2c>_<scheme>_
            // h2c : hash to curve (for hash to point and hash pubkey to point)
            // scheme : tag NUL (basic), AUG, POP
            // signature variant
            // pairing friendly elliptic curve
            // hash function
            // BLS12381G1-SHA256-SSWU-RO- (min sig)
            // BLS12381G2-SHA256-SSWU-RO- (min pub key)
            // scheme = basic, curve = BLS12-381, hash = SHA-256

            // Signature variant = MinimalPublicKeySize

            // Pairing-friendly elliptic curve

            // Hash function = Sha256

            // HashToPoint function (hash to G2, for min pub key)

            // Signature Schemes: Basic, MessageAugmentation, ProofOfPossession

            // Standard hash-to-point values
            // ETH 2 hash to g2 function
            // Simplified SWU for pairing-friendly curves
            // -RO uses hash_to_curve, required for random oracle
            // -NU uses non-unniform, encode_to_curve

            LegalKeySizesValue = new[] { new KeySizes(32 * 8, 32 * 8, 0) };
        }

        /// <summary>
        /// Gets the curve name part of the algorithm name, e.g. "BLS12381"
        /// </summary>
        public abstract string CurveName { get; }

        /// <summary>
        /// Gets the hash algorithm to use; usually SHA256 (the default).
        /// </summary>
        public HashAlgorithm HashAlgorithm
        {
            get
            {
                return _hashAlgorithm;
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Gets the hash-to-point name, part of the hash to curve suite, e.g. "SSWU-RO-"
        /// </summary>
        public abstract string HashToPointName { get; }

        /// <summary>
        /// Gets the scheme used to defend against rogue key attacks (basic, message augmentation, or poof of possession).
        /// </summary>
        public abstract BlsScheme Scheme { get; }

        /// <inheritdoc />
        public override string SignatureAlgorithm
        {
            get
            {
                VariantTags.TryGetValue(Variant, out var variantTag);
                var hashToCurveSuite = $"{CurveName}{variantTag}-{HashAlgorithm}-{HashToPointName}";
                SchemeTags.TryGetValue(Scheme, out var schemeTag);
                var algorithmName = $"BLS_SIG_{hashToCurveSuite}_{schemeTag}_";
                return algorithmName;
            }
        }

        /// <summary>
        /// Gets the variant used: minimal signature size (G1 signatures) or minimal public key size (G2 signatures) used.
        /// </summary>
        public abstract BlsVariant Variant { get; }

        /// <summary>
        /// Gets the tags used in the name to represent the scheme.
        /// </summary>
        protected static IReadOnlyDictionary<BlsScheme, string> SchemeTags => new Dictionary<BlsScheme, string> {
            { BlsScheme.Basic, "NUL"},
            { BlsScheme.MessageAugmentation, "AUG"},
            { BlsScheme.ProofOfPossession, "POP"},
        };

        /// <summary>
        /// Gets the tags used in the name to represent the variant.
        /// </summary>
        protected static IReadOnlyDictionary<BlsVariant, string> VariantTags => new Dictionary<BlsVariant, string> {
            { BlsVariant.MinimalSignatureSize, "G1"},
            { BlsVariant.MinimalPublicKeySize, "G2"},
        };

        /// <summary>
        /// Creates a new BLS asymmetric algorithm with the specified parameters.
        /// </summary>
        public static BLS Create(BLSParameters parameters)
        {
            // TODO: Load default from config (not sure how new .NET Core does it; old Framework used to use app.config)
            return new BLSHerumi(parameters);
        }

        /// <summary>
        /// Imports the specified parameters into the current BLS asymmetric algorithm.
        /// </summary>
        public abstract void ImportParameters(BLSParameters parameters);

        /// <summary>
        /// Combines the provided public keys into a single public key value.
        /// </summary>
        /// <param name="publicKeys">Byte span of concatenated public key bytes; must be a multiple of the public key length.</param>
        /// <param name="destination">Span to write the combined public key to.</param>
        /// <param name="bytesWritten">Output the number of bytes written.</param>
        /// <returns>true if the public key aggregation was successful; false if the destination is not large enough to hold the result</returns>
        public abstract bool TryAggregatePublicKeys(ReadOnlySpan<byte> publicKeys, Span<byte> destination, out int bytesWritten);

        /// <summary>
        /// Combines the provided signatures into a single signature value.
        /// </summary>
        /// <param name="signatures">Byte span of concatenated signature bytes; must be a multiple of the signature length.</param>
        /// <param name="destination">Span to write the combined signature to.</param>
        /// <param name="bytesWritten">Output the number of bytes written.</param>
        /// <returns>true if the signature aggregation was successful; false if the destination is not large enough to hold the result</returns>
        public abstract bool TryAggregateSignatures(ReadOnlySpan<byte> signatures, Span<byte> destination, out int bytesWritten);

        /// <summary>
        /// Gets the serialized private (secret) key, if available.
        /// </summary>
        /// <param name="destination">Span to write the key to.</param>
        /// <param name="bytesWritten">Output the number of bytes written.</param>
        /// <returns>true if the private key is available; false if the private key is not available or if the destination is not large enough to hold the result</returns>
        public abstract bool TryExportBLSPrivateKey(Span<byte> destination, out int bytesWritten);

        /// <summary>
        /// Gets the serialized public key.
        /// </summary>
        /// <param name="destination">Span to write the key to.</param>
        /// <param name="bytesWritten">Output the number of bytes written.</param>
        /// <returns>true if successful; false if the destination is not large enough to hold the result</returns>
        public abstract bool TryExportBLSPublicKey(Span<byte> destination, out int bytesWritten);

        /// <summary>
        /// Hash and then sign the data (with optional domain).
        /// </summary>
        /// <param name="data">The data to sign.</param>
        /// <param name="destination">Span to write the signature to.</param>
        /// <param name="bytesWritten">Output the number of bytes written.</param>
        /// <param name="domain">Optional additional data for the hash to point function (if needed).</param>
        /// <returns>true if the signing was successful; false if the destination is not large enough to hold the result</returns>
        public abstract bool TrySignData(ReadOnlySpan<byte> data, Span<byte> destination, out int bytesWritten, ReadOnlySpan<byte> domain = default);

        /// <summary>
        /// Sign the specified hash (with optional domain), using the current private (secret) key.
        /// </summary>
        /// <param name="hash">The hash to sign.</param>
        /// <param name="destination">Span to write the signature to.</param>
        /// <param name="bytesWritten">Output the number of bytes written.</param>
        /// <param name="domain">Optional additional data for the hash to point function (if needed).</param>
        /// <returns>true if the signing was successful; false if the destination is not large enough to hold the result</returns>
        public abstract bool TrySignHash(ReadOnlySpan<byte> hash, Span<byte> destination, out int bytesWritten, ReadOnlySpan<byte> domain = default);

        /// <summary>
        /// Checks the provided aggregate signature matches the combined provided public keys and hashes (with optional domain).
        /// </summary>
        /// <param name="publicKeys">Span of concatenated public key bytes; must be a multiple of the public key length.</param>
        /// <param name="hashes">Span of concatenated hashes; length divided evenly by the number of public keys, so all hashes must be the same length.</param>
        /// <param name="aggregateSignature">The aggregate signature to check against the data.</param>
        /// <param name="domain">Optional additional data for the hash to point function (if needed).</param>
        /// <returns>true if the aggregate signature is valid; false if invalid</returns>
        public abstract bool VerifyAggregate(ReadOnlySpan<byte> publicKeys, ReadOnlySpan<byte> hashes, ReadOnlySpan<byte> aggregateSignature, ReadOnlySpan<byte> domain = default);

        /// <summary>
        /// Checks the provided signature matches the hash of the specified data (with optional domain), using the current public key.
        /// </summary>
        /// <param name="data">The data that was signed.</param>
        /// <param name="signature">The signature to check against the data.</param>
        /// <param name="domain">Optional additional data for the hash to point function (if needed).</param>
        /// <returns>true if the signature is valid; false if invalid</returns>
        public abstract bool VerifyData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> domain = default);

        /// <summary>
        /// Checks the provided signature matches the specified hash (with optional domain), using the current public key.
        /// </summary>
        /// <param name="hash">The hash that was signed.</param>
        /// <param name="signature">The signature to check against the data.</param>
        /// <param name="domain">Optional additional data for the hash to point function (if needed).</param>
        /// <returns>true if the signature is valid; false if invalid</returns>
        public abstract bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> domain = default);
    }
}
