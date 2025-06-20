// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Logging;
using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;

namespace Nethermind.Crypto;

internal class BlobProofsManagerV0 : IBlobProofsManager
{
    public static BlobProofsManagerV0 Instance { get; } = new BlobProofsManagerV0();

    public ShardBlobNetworkWrapper AllocateWrapper(params ReadOnlySpan<byte[]> blobs)
    {
        ShardBlobNetworkWrapper result = new(blobs.ToArray(), new byte[blobs.Length][], new byte[blobs.Length][], ProofVersion.V0);

        for (int i = 0; i < blobs.Length; i++)
        {
            result.Commitments[i] = new byte[Ckzg.BytesPerCommitment];
            result.Proofs[i] = new byte[Ckzg.BytesPerProof];
        }

        return result;
    }

    public void ComputeProofsAndCommitments(ShardBlobNetworkWrapper wrapper)
    {
        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            Ckzg.BlobToKzgCommitment(wrapper.Commitments[i], wrapper.Blobs[i], _ckzgSetup);
            Ckzg.ComputeBlobKzgProof(wrapper.Proofs[i], wrapper.Blobs[i], wrapper.Commitments[i], _ckzgSetup);
        }
    }


    internal static IntPtr _ckzgSetup = IntPtr.Zero;

    private static Task? _initializeTask;

    public static bool IsInitialized => _ckzgSetup != IntPtr.Zero;

    public Task InitAsync(ILogger logger = default) => _initializeTask ??= Task.Run(() =>
    {
        if (_ckzgSetup != IntPtr.Zero) return;

        string trustedSetupTextFileLocation = Path.Combine(Path.GetDirectoryName(typeof(KzgPolynomialCommitments).Assembly.Location) ??
                         string.Empty, "kzg_trusted_setup.txt");

        if (logger.IsInfo)
            logger.Info($"Loading {nameof(Ckzg)} trusted setup from file {trustedSetupTextFileLocation}");
        _ckzgSetup = Ckzg.LoadTrustedSetup(trustedSetupTextFileLocation, 8);

        if (_ckzgSetup == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to load trusted setup");
        }
    });

    public bool ValidateLengths(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Blobs.Length != wrapper.Commitments.Length || wrapper.Blobs.Length != wrapper.Proofs.Length)
        {
            return false;
        }

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            if (wrapper.Blobs[i].Length != Ckzg.BytesPerBlob || wrapper.Commitments[i].Length != Ckzg.BytesPerCommitment || wrapper.Proofs[i].Length != Ckzg.BytesPerProof)
            {
                return false;
            }
        }

        return true;
    }

    public bool ValidateProofs(ShardBlobNetworkWrapper wrapper)
    {
        if (wrapper.Blobs.Length is 1 && wrapper.Commitments.Length is 1 && wrapper.Proofs.Length is 1)
        {
            try
            {
                return Ckzg.VerifyBlobKzgProof(wrapper.Blobs[0], wrapper.Commitments[0], wrapper.Proofs[0], _ckzgSetup);
            }
            catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
            {
                return false;
            }
        }

        int length = wrapper.Blobs.Length * Ckzg.BytesPerBlob;
        using ArrayPoolSpan<byte> flatBlobs = new(length);

        length = wrapper.Blobs.Length * Ckzg.BytesPerCommitment;
        using ArrayPoolSpan<byte> flatCommitments = new(length);

        length = wrapper.Blobs.Length * Ckzg.BytesPerProof;
        using ArrayPoolSpan<byte> flatProofs = new(length);

        for (int i = 0; i < wrapper.Blobs.Length; i++)
        {
            wrapper.Blobs[i].CopyTo(flatBlobs.Slice(i * Ckzg.BytesPerBlob, Ckzg.BytesPerBlob));
            wrapper.Commitments[i].CopyTo(flatCommitments.Slice(i * Ckzg.BytesPerCommitment, Ckzg.BytesPerCommitment));
            wrapper.Proofs[i].CopyTo(flatProofs.Slice(i * Ckzg.BytesPerProof, Ckzg.BytesPerProof));
        }

        try
        {
            return Ckzg.VerifyBlobKzgProofBatch(flatBlobs, flatCommitments, flatProofs, wrapper.Blobs.Length, _ckzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }

    public bool VerifyProof(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> z, ReadOnlySpan<byte> y, ReadOnlySpan<byte> proof)
    {
        try
        {
            return Ckzg.VerifyKzgProof(commitment, z, y, proof, _ckzgSetup);
        }
        catch (Exception e) when (e is ArgumentException or ApplicationException or InsufficientMemoryException)
        {
            return false;
        }
    }
}
