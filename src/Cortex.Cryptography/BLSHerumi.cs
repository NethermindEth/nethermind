using System;
using System.Security.Cryptography;

namespace Cortex.Cryptography
{
    public class BLSHerumi : BLS
    {
        private const int HashLength = 32;
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

        // Not sure if this is correct, ETH2.0 uses a custom function for hash to G2.
        /// <inheritdoc />
        public override string HashToPointName => "SSWU-RO-";

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
        public override bool TryAggregate(ReadOnlySpan<byte> signatures, Span<byte> destination, out int bytesWritten)
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

        /*
        public override bool TrySignData(ReadOnlySpan<byte> data, Span<byte> destination, out int bytesWritten, byte[]? domain = null)
        {
            if (destination.Length < SignatureLength)
            {
                bytesWritten = 0;
                return false;
            }

            EnsureInitialised();

            // TODO: Generate random key if null
            // EnsurePrivateKey();

            var bytesRead = Bls384Interop.blsSecretKeyDeserialize(out var blsSecretKey, _privateKey!, _privateKey!.Length);
            if (bytesRead != _privateKey.Length)
            {
                throw new Exception($"Error deserializing BLS private key, length: {bytesRead}");
            }

            var result = Bls384Interop.blsSign(out var blsSignature, blsSecretKey, data.ToArray(), data.Length);
            if (result != 0)
            {
                throw new Exception($"Error generating BLS signature for data. Error: {result}");
            }

            var buffer = new byte[SignatureLength];
            bytesWritten = Bls384Interop.blsSignatureSerialize(buffer, buffer.Length, blsSignature);
            if (bytesWritten != buffer.Length)
            {
                throw new Exception($"Error serializing BLS signature, length: {bytesWritten}");
            }
            buffer.CopyTo(destination);
            return true;
        }
        */

        /// <inheritdoc />
        public override bool TrySignHash(ReadOnlySpan<byte> hash, Span<byte> destination, out int bytesWritten, byte[]? domain = null)
        {
            // NOTE: Should be based on the hash algorithm
            //if (hash.Length != HashLength)
            //{
            //    throw new ArgumentOutOfRangeException(nameof(hash), hash.Length, $"Hash must be {HashLength} bytes long.");
            //}
            if (destination.Length < SignatureLength)
            {
                bytesWritten = 0;
                return false;
            }

            EnsureInitialised();

            // TODO: Generate random key if null
            // EnsurePrivateKey();

            var bytesRead = Bls384Interop.blsSecretKeyDeserialize(out var blsSecretKey, _privateKey!, _privateKey!.Length);
            if (bytesRead != _privateKey.Length)
            {
                throw new Exception($"Error deserializing BLS private key, length: {bytesRead}");
            }

            var result = Bls384Interop.blsSignHash(out var blsSignature, blsSecretKey, hash.ToArray(), hash.Length);
            if (result != 0)
            {
                throw new Exception($"Error generating BLS signature for hash. Error: {result}");
            }

            var buffer = new byte[SignatureLength];
            bytesWritten = Bls384Interop.blsSignatureSerialize(buffer, buffer.Length, blsSignature);
            if (bytesWritten != buffer.Length)
            {
                throw new Exception($"Error serializing BLS signature, length: {bytesWritten}");
            }
            buffer.CopyTo(destination);
            return true;
        }

        /// <inheritdoc />
        public override bool VerifyAggregate(ReadOnlySpan<byte> publicKeys, ReadOnlySpan<byte> hashes, ReadOnlySpan<byte> signature, byte[]? domain = null)
        {
            // This is going to ignore the public (if any) and verify the provided public keys.
            throw new NotImplementedException();
        }

        /*
        public override bool VerifyData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, byte[]? domain = null)
        {
            if (signature.Length != SignatureLength)
            {
                throw new ArgumentOutOfRangeException(nameof(signature), signature.Length, $"Signature must be {SignatureLength} bytes long.");
            }
            // NOTE: Should be based on the hash algorithm
            //if (hash.Length != HashLength)
            //{
            //    throw new ArgumentOutOfRangeException(nameof(hash), hash.Length, $"Hash must be {HashLength} bytes long.");
            //}

            EnsureInitialised();
            EnsurePublicKey();

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

            var result = Bls384Interop.blsVerify(blsSignature, blsPublicKey, data.ToArray(), data.Length);

            return (result == 1);
        }
        */

        /// <inheritdoc />
        public override bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, byte[]? domain = null)
        {
            if (signature.Length != SignatureLength)
            {
                throw new ArgumentOutOfRangeException(nameof(signature), signature.Length, $"Signature must be {SignatureLength} bytes long.");
            }
            // NOTE: Should be based on the hash algorithm
            //if (hash.Length != HashLength)
            //{
            //    throw new ArgumentOutOfRangeException(nameof(hash), hash.Length, $"Hash must be {HashLength} bytes long.");
            //}

            EnsureInitialised();
            EnsurePublicKey();

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

            var result = Bls384Interop.blsVerifyHash(blsSignature, blsPublicKey, hash.ToArray(), hash.Length);

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

                    // NOTE: Standard mentions big endian encoding, but not clear without test cases.
                    // This does little endian, same as rest of ETH 2.0
                    // (actually, it does platform endian; so need to fix either way to ensure big/little as needed)

                    // Generated keys have last two as zero, so implying LE ?

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
