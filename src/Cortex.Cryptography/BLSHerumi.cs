using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static Cortex.Cryptography.Bls384;

namespace Cortex.Cryptography
{
    public class BLSHerumi : BLS
    {
        private byte[]? _privateKey;
        private byte[]? _publicKey;

        public BLSHerumi(BLSParameters parameters)
        {
            if (parameters.PublicKey != null && parameters.PublicKey.Length != 48)
            {
                throw new ArgumentOutOfRangeException(nameof(parameters.PublicKey), parameters.PublicKey.Length, "Public key must be 48 bytes long.");
            }

            _privateKey = parameters.PrivateKey?.AsSpan().ToArray();
            _publicKey = parameters.PublicKey?.AsSpan().ToArray();
        }

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

        public override bool TrySignHash(ReadOnlySpan<byte> hash, Span<byte> destination, out int bytesWritten)
        {
            throw new NotImplementedException();
        }

        public override bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature)
        {
            if (signature.Length != 96)
            {
                throw new ArgumentOutOfRangeException(nameof(signature), signature.Length, "Signature must be 96 bytes long.");
            }
            // NOTE: Should be based on the hash algorithm
            if (hash.Length != 32)
            {
                throw new ArgumentOutOfRangeException(nameof(hash), hash.Length, "Hash must be 32 bytes long.");
            }

            // NOTE: Should check we have _publicKey (or generate from _privateKey if needed)

            // NOTE: Should flag if library has been initialised yet
            var initialiseResult = blsInit(MCL_BLS12_381, MCLBN_COMPILED_TIME_VAR);
            if (initialiseResult != 0)
            {
                throw new Exception($"Error initialising BLS algorithm. Error {initialiseResult}");
            }

            var blsPublicKey = new BlsPublicKey();
            var blsPublicKeySpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref blsPublicKey, 1));
            _publicKey.CopyTo(blsPublicKeySpan);

            var blsSignature = new BlsSignature();
            var blsSignatureSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref blsSignature, 1));
            signature.CopyTo(blsPublicKeySpan);

            var result = blsVerifyHash(blsSignature, blsPublicKey, hash.ToArray(), hash.Length);

            return (result == 1);
        }

        private void EnsurePublicKey()
        {
            if (_publicKey == null)
            {
                if (_privateKey != null)
                {
                    var initialiseResult = blsInit(MCL_BLS12_381, MCLBN_COMPILED_TIME_VAR);
                    if (initialiseResult != 0)
                    {
                        throw new Exception($"Error initialising BLS algorithm. Error {initialiseResult}");
                    }

                    var blsSecretKey = new BlsSecretKey();
                    var blsSecretKeySpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref blsSecretKey, 1));
                    _privateKey.CopyTo(blsSecretKeySpan);

                    var blsPublicKey = new BlsPublicKey();
                    blsGetPublicKey(out blsPublicKey, blsSecretKey);

                    var blsPublicKeySpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref blsPublicKey, 1));
                    _publicKey = blsPublicKeySpan.ToArray();
                }
            }
        }
    }
}
