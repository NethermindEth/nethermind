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
    public static readonly UInt256 BlsModulus =
        UInt256.Parse("52435875175126190479447740508185965837690552500527637822603658699938581184513",
            System.Globalization.NumberStyles.Integer);

    private const byte KzgBlobHashVersionV1 = 1;
    private static IntPtr _ckzgSetup = IntPtr.Zero;

    private static readonly ThreadLocal<SHA256> _sha256 = new(SHA256.Create);

    private static Task? _initializeTask;

    public static Task Initialize(ILogger? logger = null) => _initializeTask ??= Task.Run(() =>
    {
        if (_ckzgSetup != IntPtr.Zero) return;

        string trustedSetupTextFileLocation =
            Path.Combine(Path.GetDirectoryName(typeof(KzgPolynomialCommitments).Assembly.Location) ??
                         string.Empty, "kzg_trusted_setup.txt");

        if (logger?.IsInfo == true)
            logger.Info($"Loading {nameof(Ckzg)} trusted setup from file {trustedSetupTextFileLocation}");
        _ckzgSetup = Ckzg.Ckzg.LoadTrustedSetup(trustedSetupTextFileLocation);

        if (_ckzgSetup == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to load trusted setup");
        }
    });

    /// <summary>
    ///
    /// </summary>
    /// <param name="commitment">Commitment to calculate hash from</param>
    /// <param name="hashBuffer">Holds the output, can safely contain any data before the call.</param>
    /// <returns>Result of the attempt</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryComputeCommitmentV1(ReadOnlySpan<byte> commitment, Span<byte> hashBuffer)
    {
        const int bytesPerHash = 32;

        if (commitment.Length != Ckzg.Ckzg.BytesPerCommitment)
        {
            throw new ArgumentException($"Commitment should be {Ckzg.Ckzg.BytesPerCommitment} bytes",
                nameof(commitment));
        }

        if (hashBuffer.Length != bytesPerHash)
        {
            throw new ArgumentException($"Commitment should be {bytesPerHash} bytes", nameof(hashBuffer));
        }

        if (_sha256.Value!.TryComputeHash(commitment, hashBuffer, out _))
        {
            hashBuffer[0] = KzgBlobHashVersionV1;
            return true;
        }

        return false;
    }

    public static bool VerifyProof(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> z, ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof)
    {
        try
        {
            return Ckzg.Ckzg.VerifyKzgProof(commitment, z, y, proof, _ckzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }

    public static bool AreProofsValid(byte[] blobs, byte[] commitments, byte[] proofs)
    {
        try
        {
            return Ckzg.Ckzg.VerifyBlobKzgProofBatch(blobs, commitments, proofs, blobs.Length / Ckzg.Ckzg.BytesPerBlob,
                _ckzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
