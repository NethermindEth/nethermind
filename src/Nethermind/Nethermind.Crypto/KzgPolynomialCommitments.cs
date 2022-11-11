// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Nethermind.Crypto.Properties;
using Nethermind.Int256;

namespace Nethermind.Crypto
{
    public static class KzgPolynomialCommitments
    {
        public static readonly UInt256 BlsModulus = UInt256.Parse("73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001", System.Globalization.NumberStyles.HexNumber);
        public static readonly ulong FieldElementsPerBlob = 4096;

        private const byte KzgBlobHashVersionV1 = 1;
        private static IntPtr _ckzgSetup = IntPtr.Zero;

        private static readonly ThreadLocal<SHA256> _sha256 = new();

        private static readonly object _inititalizeLock = new();
        public static void Inititalize()
        {
            lock (_inititalizeLock)
            {
                if (_ckzgSetup != IntPtr.Zero)
                {
                    return;
                }
                string tmpFilename = Path.GetTempFileName();
                using FileStream tmpFileStream = new(tmpFilename, FileMode.OpenOrCreate, FileAccess.Write);
                using TextWriter tmpFileWriter = new StreamWriter(tmpFileStream);
                tmpFileWriter.Write(Resources.kzg_trusted_setup);
                tmpFileWriter.Close();
                tmpFileStream.Close();
                _ckzgSetup = Ckzg.Ckzg.LoadTrustedSetup(tmpFilename);
                File.Delete(tmpFilename);
            }
        }

        public static Span<byte> CommitmentToHashV1(ReadOnlySpan<byte> data_kzg)
        {
            if (!_sha256.IsValueCreated)
            {
                SHA256 sha = SHA256.Create();
                sha.Initialize();
                _sha256.Value = sha;
            }

            byte[] hash = _sha256.Value.ComputeHash(data_kzg.ToArray());
            hash[0] = KzgBlobHashVersionV1;
            return hash;
        }

        public static unsafe bool VerifyProof(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> z, ReadOnlySpan<byte> y, ReadOnlySpan<byte> proof)
        {
            fixed (byte* commitmentPtr = commitment, zPtr = z, yPtr = y, proofPtr = proof)
            {
                return Ckzg.Ckzg.VerifyKzgProof(commitmentPtr, zPtr, yPtr, proofPtr, _ckzgSetup) == 0;
            }
        }
    }
}
