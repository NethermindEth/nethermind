// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Crypto;

public static class KzgPolynomialCommitments
{
    // https://eips.ethereum.org/EIPS/eip-4844#parameters
    public static readonly UInt256 BlsModulus =
        UInt256.Parse("52435875175126190479447740508185965837690552500527637822603658699938581184513",
            System.Globalization.NumberStyles.Integer);

    public const byte KzgBlobHashVersionV1 = 1;
    public const byte BytesPerBlobVersionedHash = 32;

    private static IntPtr _ckzgSetup = IntPtr.Zero;

    private static readonly ThreadLocal<SHA256> _sha256 = new(SHA256.Create);

    private static Task? _initializeTask;

    public static bool IsInitialized => _ckzgSetup != IntPtr.Zero;

    public static Task InitializeAsync(ILogger logger = default, string? setupFilePath = null) => _initializeTask ??= Task.Run(() =>
    {
        if (_ckzgSetup != IntPtr.Zero) return;

        string trustedSetupTextFileLocation = setupFilePath ??
            Path.Combine(Path.GetDirectoryName(typeof(KzgPolynomialCommitments).Assembly.Location) ??
                         string.Empty, "kzg_trusted_setup.txt");

        if (logger.IsInfo)
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
    /// <param name="commitment">Hash256 to calculate hash from</param>
    /// <param name="hashBuffer">Holds the output, can safely contain any data before the call.</param>
    /// <returns>Result of the attempt</returns>
    /// <exception cref="ArgumentException"></exception>
    public static bool TryComputeCommitmentHashV1(ReadOnlySpan<byte> commitment, Span<byte> hashBuffer)
    {
        if (commitment.Length != Ckzg.Ckzg.BytesPerCommitment)
        {
            return false;
        }

        if (hashBuffer.Length != BytesPerBlobVersionedHash)
        {
            throw new ArgumentException($"{nameof(hashBuffer)} should be {BytesPerBlobVersionedHash} bytes", nameof(hashBuffer));
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

    public static bool AreProofsValid(byte[][] blobs, byte[][] commitments, byte[][] proofs)
    {
        if (blobs.Length is 1 && commitments.Length is 1 && proofs.Length is 1)
        {
            try
            {
                return Ckzg.Ckzg.VerifyBlobKzgProof(blobs[0], commitments[0], proofs[0], _ckzgSetup);
            }
            catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
            {
                return false;
            }
        }

        int length = blobs.Length * Ckzg.Ckzg.BytesPerBlob;
        byte[] flatBlobsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> flatBlobs = new(flatBlobsArray, 0, length);

        length = blobs.Length * Ckzg.Ckzg.BytesPerCommitment;
        byte[] flatCommitmentsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> flatCommitments = new(flatCommitmentsArray, 0, length);

        length = blobs.Length * Ckzg.Ckzg.BytesPerProof;
        byte[] flatProofsArray = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> flatProofs = new(flatProofsArray, 0, length);

        for (int i = 0; i < blobs.Length; i++)
        {
            blobs[i].CopyTo(flatBlobs.Slice(i * Ckzg.Ckzg.BytesPerBlob, Ckzg.Ckzg.BytesPerBlob));
            commitments[i].CopyTo(flatCommitments.Slice(i * Ckzg.Ckzg.BytesPerCommitment, Ckzg.Ckzg.BytesPerCommitment));
            proofs[i].CopyTo(flatProofs.Slice(i * Ckzg.Ckzg.BytesPerProof, Ckzg.Ckzg.BytesPerProof));
        }

        try
        {
            return Ckzg.Ckzg.VerifyBlobKzgProofBatch(flatBlobs, flatCommitments, flatProofs, blobs.Length,
                _ckzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(flatBlobsArray);
            ArrayPool<byte>.Shared.Return(flatCommitmentsArray);
            ArrayPool<byte>.Shared.Return(flatProofsArray);
        }
    }

    /// <summary>
    /// Method to generate correct data for tests only, not safe
    /// </summary>
    public static void KzgifyBlob(ReadOnlySpan<byte> blob, Span<byte> commitment, Span<byte> proof, Span<byte> hashV1)
    {
        Ckzg.Ckzg.BlobToKzgCommitment(commitment, blob, _ckzgSetup);
        Ckzg.Ckzg.ComputeBlobKzgProof(proof, blob, commitment, _ckzgSetup);
        TryComputeCommitmentHashV1(commitment, hashV1);
    }
}
