using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Cortex.Cryptography
{
    public class BLSHerumi : BLS
    {
        private const int PublicKeyLength = 48;
        private const int PrivateKeyLength = 32;
        private const int SignatureLength = 96;
        private const int HashLength = 32;

        private byte[]? _privateKey;
        private byte[]? _publicKey;

        public BLSHerumi(BLSParameters parameters)
        {
            if (parameters.PrivateKey != null && parameters.PrivateKey.Length != PrivateKeyLength)
            {
                throw new ArgumentOutOfRangeException(nameof(parameters.PrivateKey), parameters.PrivateKey.Length, $"Private key must be {PrivateKeyLength} bytes long.");
            }
            if (parameters.PublicKey != null && parameters.PublicKey.Length != PublicKeyLength)
            {
                throw new ArgumentOutOfRangeException(nameof(parameters.PublicKey), parameters.PublicKey.Length, $"Public key must be {PublicKeyLength} bytes long.");
            }

            // TODO: If both are null, generate random key??

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
            // NOTE: Should be based on the hash algorithm
            if (hash.Length != HashLength)
            {
                throw new ArgumentOutOfRangeException(nameof(hash), hash.Length, $"Hash must be {HashLength} bytes long.");
            }
            if (destination.Length < SignatureLength)
            {
                bytesWritten = 0;
                return false;
            }

            EnsureInitialised();

            // TODO: Generate random key if null
            // EnsurePrivateKey();

            var blsSecretKey = ToBlsSecretKey(_privateKey);

            var result = Bls384Interop.blsSignHash(out var blsSignature, blsSecretKey, hash.ToArray(), hash.Length);

            if (result != 0)
            {
                throw new Exception($"Error generating BLS signature for hash. Error: {result}");
            }

            if (BitConverter.IsLittleEndian)
            {
                // NOTE: Just returning X; need to check compressed and serialized functions

                BitConverter.TryWriteBytes(destination, blsSignature.v.x.d_0.d_0);
                BitConverter.TryWriteBytes(destination.Slice(8), blsSignature.v.x.d_0.d_1);
                BitConverter.TryWriteBytes(destination.Slice(16), blsSignature.v.x.d_0.d_2);
                BitConverter.TryWriteBytes(destination.Slice(24), blsSignature.v.x.d_0.d_3);
                BitConverter.TryWriteBytes(destination.Slice(32), blsSignature.v.x.d_0.d_4);
                BitConverter.TryWriteBytes(destination.Slice(40), blsSignature.v.x.d_0.d_5);
                BitConverter.TryWriteBytes(destination.Slice(48), blsSignature.v.x.d_1.d_0);
                BitConverter.TryWriteBytes(destination.Slice(56), blsSignature.v.x.d_1.d_1);
                BitConverter.TryWriteBytes(destination.Slice(64), blsSignature.v.x.d_1.d_2);
                BitConverter.TryWriteBytes(destination.Slice(72), blsSignature.v.x.d_1.d_3);
                BitConverter.TryWriteBytes(destination.Slice(80), blsSignature.v.x.d_1.d_4);
                BitConverter.TryWriteBytes(destination.Slice(88), blsSignature.v.x.d_1.d_5);
            }
            else
            {
                throw new NotImplementedException();
            }
            bytesWritten = SignatureLength;
            return true;
        }

        private Bls384Interop.BlsSecretKey ToBlsSecretKey(byte[] privateKey)
        {
            var blsSecretKey = new Bls384Interop.BlsSecretKey();
            if (BitConverter.IsLittleEndian)
            {
                blsSecretKey.v.d_0 = BitConverter.ToUInt64(privateKey);
                blsSecretKey.v.d_1 = BitConverter.ToUInt64(privateKey, 8);
                blsSecretKey.v.d_2 = BitConverter.ToUInt64(privateKey, 16);
                blsSecretKey.v.d_3 = BitConverter.ToUInt64(privateKey, 24);
            }
            else
            {
                throw new NotImplementedException();
            }
            return blsSecretKey;
        }

        private static bool _initialised;

        private static void EnsureInitialised()
        {
            if (!_initialised)
            {
                var result = Bls384Interop.blsInit(Bls384Interop.MCL_BLS12_381, Bls384Interop.MCLBN_COMPILED_TIME_VAR);
                if (result != 0)
                {
                    throw new Exception($"Error initialising BLS algorithm. Error: {result}");
                }
                _initialised = true;
            }
        }

        public override bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature)
        {
            if (signature.Length != SignatureLength)
            {
                throw new ArgumentOutOfRangeException(nameof(signature), signature.Length, $"Signature must be {SignatureLength} bytes long.");
            }
            // NOTE: Should be based on the hash algorithm
            if (hash.Length != HashLength)
            {
                throw new ArgumentOutOfRangeException(nameof(hash), hash.Length, $"Hash must be {HashLength} bytes long.");
            }

            EnsureInitialised();
            EnsurePublicKey();

            var blsPublicKey = new Bls384Interop.BlsPublicKey();
            var blsPublicKeySpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref blsPublicKey, 1));
            _publicKey.CopyTo(blsPublicKeySpan);

            var blsSignature = new Bls384Interop.BlsSignature();
            var blsSignatureSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref blsSignature, 1));
            signature.CopyTo(blsPublicKeySpan);

            var result = Bls384Interop.blsVerifyHash(blsSignature, blsPublicKey, hash.ToArray(), hash.Length);

            return (result == 1);
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

                    var blsSecretKey = ToBlsSecretKey(_privateKey);

                    // NOTE: Is this going to leak, or does the GC take care of cleaning blsPublicKey?
                    Bls384Interop.blsGetPublicKey(out var blsPublicKey, blsSecretKey);

                    var buffer = ToBytes(blsPublicKey);
                    _publicKey = buffer;
                }
            }
        }

        private static byte[] ToBytes(Bls384Interop.BlsPublicKey blsPublicKey)
        {
            var buffer = new byte[48];
            var span = new Span<byte>(buffer);
            if (BitConverter.IsLittleEndian)
            {
                BitConverter.TryWriteBytes(span, blsPublicKey.v.x.d_0);
                BitConverter.TryWriteBytes(span.Slice(8), blsPublicKey.v.x.d_1);
                BitConverter.TryWriteBytes(span.Slice(16), blsPublicKey.v.x.d_2);
                BitConverter.TryWriteBytes(span.Slice(24), blsPublicKey.v.x.d_3);
                BitConverter.TryWriteBytes(span.Slice(32), blsPublicKey.v.x.d_4);
                BitConverter.TryWriteBytes(span.Slice(40), blsPublicKey.v.x.d_5);
            }
            else
            {
                throw new NotImplementedException();
            }
            return buffer;
        }
    }
}
