// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Crypto;

public static class KzgPolynomialCommitments
{
    public static readonly UInt256 BlsModulus = UInt256.Parse("52435875175126190479447740508185965837690552500527637822603658699938581184513", System.Globalization.NumberStyles.Integer);
    public static readonly ulong FieldElementsPerBlob = 4096;

    private const byte KzgBlobHashVersionV1 = 1;
    private static IntPtr _ckzgSetup = IntPtr.Zero;

    private static readonly ThreadLocal<SHA256> _sha256 = new(SHA256.Create);

    private static Task? _initializeTask = null;

    public static Task Initialize(ILogger? logger = null) => _initializeTask ?? (_initializeTask = Task.Run(() =>
        {
            if (_ckzgSetup != IntPtr.Zero) return;

            string trustedSetupTextFileLocation =
                Path.Combine(Path.GetDirectoryName(typeof(KzgPolynomialCommitments).Assembly.Location) ??
                             string.Empty, "kzg_trusted_setup.txt");

            if (logger?.IsInfo == true) logger.Info($"Loading {nameof(Ckzg)} trusted setup from file {trustedSetupTextFileLocation}");
            _ckzgSetup = Ckzg.Ckzg.LoadTrustedSetup(trustedSetupTextFileLocation);

            if (_ckzgSetup == IntPtr.Zero)
            {
                throw new InvalidOperationException("Unable to load trusted setup");
            }
        }));

    public static bool TryComputeCommitmentV1(ReadOnlySpan<byte> commitment, Span<byte> hashBuffer)
    {
        if (_sha256.Value!.TryComputeHash(commitment, hashBuffer, out _))
        {
            hashBuffer[0] = KzgBlobHashVersionV1;
            return true;
        }

        return false;
    }

    public static unsafe bool VerifyProof(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> z, ReadOnlySpan<byte> y, ReadOnlySpan<byte> proof)
    {
        if (_ckzgSetup == IntPtr.Zero)
        {
            throw new InvalidOperationException("KZG is not initialized");
        }

        fixed (byte* commitmentPtr = commitment, zPtr = z, yPtr = y, proofPtr = proof)
        {
            return Ckzg.Ckzg.VerifyKzgProof(commitmentPtr, zPtr, yPtr, proofPtr, _ckzgSetup) == 0;
        }
    }
}
