using System;
using System.Security.Cryptography;

namespace Cortex.Cryptography
{
    public class BLSHerumi : BLS
    {
        private const int HashLength = 32;
        private const int InitialXPartLength = 48;
        private const int PrivateKeyLength = 32;
        private const int PublicKeyLength = 48;
        private const int SignatureLength = 96;
        private static bool _initialised;
        private byte[]? _privateKey;
        private byte[]? _publicKey;

        public BLSHerumi(BLSParameters parameters)
        {
            KeySizeValue = PrivateKeyLength * 8;
            ImportParameters(parameters);
        }

        /// <inheritdoc />
        public override string CurveName => "BLS12381";

        /// <inheritdoc />
        public override string HashToPointName => "ETH2-";

        /// <inheritdoc />
        public override BlsScheme Scheme => BlsScheme.Basic;

        /// <inheritdoc />
        public override BlsVariant Variant => BlsVariant.MinimalPublicKeySize;

        /// <inheritdoc />
        public override void ImportParameters(BLSParameters parameters)
        {
            if (parameters.PrivateKey != null && parameters.PrivateKey.Length != PrivateKeyLength)
            {
                throw new ArgumentOutOfRangeException(nameof(parameters.PrivateKey), parameters.PrivateKey.Length, $"Private key must be {PrivateKeyLength} bytes long.");
            }
            if (parameters.PublicKey != null && parameters.PublicKey.Length != PublicKeyLength)
            {
                throw new ArgumentOutOfRangeException(nameof(parameters.PublicKey), parameters.PublicKey.Length, $"Public key must be {PublicKeyLength} bytes long.");
            }

            if (parameters.InputKeyMaterial != null)
            {
                throw new NotSupportedException("BLS input key material not supported.");
            }

            if (parameters.Variant != BlsVariant.Unknown
                && parameters.Variant != BlsVariant.MinimalPublicKeySize)
            {
                throw new NotSupportedException($"BLS variant {parameters.Variant} not supported.");
            }

            if (parameters.Scheme != BlsScheme.Unknown
               && parameters.Scheme != BlsScheme.Basic)
            {
                throw new NotSupportedException($"BLS scheme {parameters.Scheme} not supported.");
            }

            // TODO: If both are null, generate random key??

            _privateKey = parameters.PrivateKey?.AsSpan().ToArray();
            _publicKey = parameters.PublicKey?.AsSpan().ToArray();
        }

        /// <inheritdoc />
        public override bool TryAggregateSignatures(ReadOnlySpan<byte> signatures, Span<byte> destination, out int bytesWritten)
        {
            // This is independent of the keys set, although other parameters (type of curve, variant, scheme, etc) are relevant.
            if (signatures.Length % SignatureLength != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(signatures), signatures.Length, $"Signature data must be a multiple of the signature length {SignatureLength}.");
            }
            if (destination.Length < SignatureLength)
            {
                bytesWritten = 0;
                return false;
            }

            EnsureInitialised();

            Bls384Interop.BlsSignature aggregateBlsSignature = default;
            for (var index = 0; index < signatures.Length; index += SignatureLength)
            {
                var signatureBytesRead = Bls384Interop.blsSignatureDeserialize(out var blsSignature, signatures.Slice(index, SignatureLength).ToArray(), SignatureLength);
                if (signatureBytesRead != SignatureLength)
                {
                    throw new Exception($"Error deserializing BLS signature, length: {signatureBytesRead}");
                }
                if (index == 0)
                {
                    aggregateBlsSignature = blsSignature;
                }
                else
                {
                    Bls384Interop.blsSignatureAdd(ref aggregateBlsSignature, blsSignature);
                }
            }

            var buffer = new byte[SignatureLength];
            bytesWritten = Bls384Interop.blsSignatureSerialize(buffer, buffer.Length, aggregateBlsSignature);
            if (bytesWritten != buffer.Length)
            {
                throw new Exception($"Error serializing BLS signature, length: {bytesWritten}");
            }
            buffer.CopyTo(destination);
            return true;
        }

        /// <summary>
        /// Combines a hash and domain to the input format used by Herumi (the initial X value)
        /// </summary>
        public bool TryCombineHashAndDomain(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> domain, Span<byte> destination, out int bytesWritten)
        {
            if (destination.Length < 2 * InitialXPartLength)
            {
                bytesWritten = 0;
                return false;
            }

            var xRealInput = new Span<byte>(new byte[hash.Length + domain.Length + 1]);
            hash.CopyTo(xRealInput);
            domain.CopyTo(xRealInput.Slice(hash.Length));
            xRealInput[hash.Length + domain.Length] = 0x01;
            var xReal = new byte[HashLength];
            var xRealSuccess = HashAlgorithm.TryComputeHash(xRealInput, xReal, out var xRealBytesWritten);
            if (!xRealSuccess || xRealBytesWritten != HashLength)
            {
                throw new Exception("Error in getting G2 real component from hash.");
            }

            var xImaginaryInput = new Span<byte>(new byte[hash.Length + domain.Length + 1]);
            hash.CopyTo(xImaginaryInput);
            domain.CopyTo(xImaginaryInput.Slice(hash.Length));
            xImaginaryInput[hash.Length + domain.Length] = 0x02;
            var xImaginary = new byte[HashLength];
            var xImaginarySuccess = HashAlgorithm.TryComputeHash(xImaginaryInput, xImaginary, out var xImaginaryBytesWritten);
            if (!xImaginarySuccess || xImaginaryBytesWritten != HashLength)
            {
                throw new Exception("Error in getting G2 imaginary component from hash.");
            }

            // Initial x value is an Fp2 value, x_re + x_im * i
            // Big endian, so put in last 32 bytes of each part
            xImaginary.CopyTo(destination.Slice(InitialXPartLength - HashLength));
            xReal.CopyTo(destination.Slice(2 * InitialXPartLength - HashLength));
            bytesWritten = 2 * InitialXPartLength;
            return true;
        }

        /// <inheritdoc />
        public override bool TryExportBLSPrivateKey(Span<byte> desination, out int bytesWritten)
        {
            if (_privateKey == null)
            {
                throw new CryptographicException("The key could not be exported.");
            }
            if (desination.Length < _privateKey.Length)
            {
                bytesWritten = 0;
                return false;
            }
            _privateKey.CopyTo(desination);
            bytesWritten = _privateKey.Length;
            return true;
        }

        /// <inheritdoc />
        public override bool TryExportBLSPublicKey(Span<byte> desination, out int bytesWritten)
        {
            EnsurePublicKey();
            if (_publicKey == null)
            {
                throw new CryptographicException("The key could not be exported.");
            }
            if (desination.Length < _publicKey.Length)
            {
                bytesWritten = 0;
                return false;
            }
            _publicKey.CopyTo(desination);
            bytesWritten = _publicKey.Length;
            return true;
        }

        public override bool TrySignData(ReadOnlySpan<byte> data, Span<byte> destination, out int bytesWritten, ReadOnlySpan<byte> domain = default)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override bool TrySignHash(ReadOnlySpan<byte> hash, Span<byte> destination, out int bytesWritten, ReadOnlySpan<byte> domain = default)
        {
            if (destination.Length < SignatureLength)
            {
                bytesWritten = 0;
                return false;
            }

            EnsureInitialised();

            var hashToSign = new byte[2 * InitialXPartLength];
            if (domain.Length > 0)
            {
                var combineSuccess = TryCombineHashAndDomain(hash, domain, hashToSign, out var combineBytesWritten);
                if (!combineSuccess || combineBytesWritten != 2 * InitialXPartLength)
                {
                    throw new Exception("Error combining the hash and domain.");
                }
            }
            else
            {
                hash.CopyTo(hashToSign);
            }

            // TODO: Generate random key if null
            // EnsurePrivateKey();

            var bytesRead = Bls384Interop.blsSecretKeyDeserialize(out var blsSecretKey, _privateKey!, _privateKey!.Length);
            if (bytesRead != _privateKey.Length)
            {
                throw new Exception($"Error deserializing BLS private key, length: {bytesRead}");
            }

            var result = Bls384Interop.blsSignHash(out var blsSignature, blsSecretKey, hashToSign, hashToSign.Length);
            if (result != 0)
            {
                throw new Exception($"Error generating BLS signature for hash. Error: {result}");
            }

            var signatureBuffer = new byte[SignatureLength];
            bytesWritten = Bls384Interop.blsSignatureSerialize(signatureBuffer, signatureBuffer.Length, blsSignature);
            if (bytesWritten != signatureBuffer.Length)
            {
                throw new Exception($"Error serializing BLS signature, length: {bytesWritten}");
            }
            signatureBuffer.CopyTo(destination);
            return true;
        }

        /// <inheritdoc />
        public override bool VerifyAggregate(ReadOnlySpan<byte> publicKeys, ReadOnlySpan<byte> hashes, ReadOnlySpan<byte> aggregateSignature, ReadOnlySpan<byte> domain = default)
        {
            // This is going to ignore the public (if any) and verify the provided public keys.
            throw new NotImplementedException();
        }

        public override bool VerifyData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> domain = default)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> domain = default)
        {
            if (signature.Length != SignatureLength)
            {
                throw new ArgumentOutOfRangeException(nameof(signature), signature.Length, $"Signature must be {SignatureLength} bytes long.");
            }

            EnsureInitialised();
            EnsurePublicKey();

            var hashToCheck = new byte[2 * InitialXPartLength];
            if (domain.Length > 0)
            {
                var combineSuccess = TryCombineHashAndDomain(hash, domain, hashToCheck, out var combineBytesWritten);
                if (!combineSuccess || combineBytesWritten != 2 * InitialXPartLength)
                {
                    throw new Exception("Error combining the hash and domain.");
                }
            }
            else
            {
                hash.CopyTo(hashToCheck);
            }

            var publicKeyBytesRead = Bls384Interop.blsPublicKeyDeserialize(out var blsPublicKey, _publicKey!, _publicKey!.Length);
            if (publicKeyBytesRead != _publicKey.Length)
            {
                throw new Exception($"Error deserializing BLS public key, length: {publicKeyBytesRead}");
            }

            var signatureBytesRead = Bls384Interop.blsSignatureDeserialize(out var blsSignature, signature.ToArray(), signature.Length);
            if (signatureBytesRead != signature.Length)
            {
                throw new Exception($"Error deserializing BLS signature, length: {signatureBytesRead}");
            }

            var result = Bls384Interop.blsVerifyHash(blsSignature, blsPublicKey, hashToCheck, hashToCheck.Length);

            return (result == 1);
        }

        private static void EnsureInitialised()
        {
            if (!_initialised)
            {
                var result = Bls384Interop.blsInit(Bls384Interop.MCL_BLS12_381, Bls384Interop.MCLBN_COMPILED_TIME_VAR);
                if (result != 0)
                {
                    throw new Exception($"Error initialising BLS algorithm. Error: {result}");
                }
                Bls384Interop.blsSetETHserialization(1);
                _initialised = true;
            }
        }

        private void EnsurePublicKey()
        {
            if (_publicKey == null)
            {
                if (_privateKey != null)
                {
                    EnsureInitialised();

                    // Standard values are big endian encoding (are using the Herumi deserialize / serialize)

                    var bytesRead = Bls384Interop.blsSecretKeyDeserialize(out var blsSecretKey, _privateKey, _privateKey.Length);
                    if (bytesRead != _privateKey.Length)
                    {
                        throw new Exception($"Error deserializing BLS private key, length: {bytesRead}");
                    }

                    Bls384Interop.blsGetPublicKey(out var blsPublicKey, blsSecretKey);

                    var buffer = new byte[PublicKeyLength];
                    var bytesWritten = Bls384Interop.blsPublicKeySerialize(buffer, buffer.Length, blsPublicKey);
                    if (bytesWritten != buffer.Length)
                    {
                        throw new Exception($"Error serializing BLS public key, length: {bytesWritten}");
                    }
                    _publicKey = buffer;
                }
            }
        }
    }
}
