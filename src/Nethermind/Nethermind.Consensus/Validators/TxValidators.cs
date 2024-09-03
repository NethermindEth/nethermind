// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Messages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public sealed class AllTxValidator(List<ITxValidator> validators) : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        foreach (ITxValidator validator in validators)
        {
            if (!validator.IsWellFormed(transaction, releaseSpec, out error))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class IntrinsicGasTxValidator : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        // This is unnecessarily calculated twice - at validation and execution times.
        var intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, releaseSpec);
        if (transaction.GasLimit < intrinsicGas)
        {
            error = TxErrorMessages.IntrinsicGasTooLow;
            return false;
        }

        return true;
    }
}

public sealed class ReleaseSpecTxValidator(Func<IReleaseSpec, bool> validate) : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        if (!validate(releaseSpec))
        {
            error = TxErrorMessages.InvalidTxType(releaseSpec.Name);
            return false;
        }

        return true;
    }
}

public sealed class ExpectedChainIdTxValidator(ulong chainId) : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        if (transaction.ChainId != chainId)
        {
            error = TxErrorMessages.InvalidTxChainId(chainId, transaction.ChainId);
            return false;
        }

        return true;
    }
}

public sealed class GasFieldsTxValidator : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (!releaseSpec.IsEip1559Enabled)
        {
            return true;
        }

        if (transaction.MaxFeePerGas < transaction.MaxPriorityFeePerGas)
        {
            error = TxErrorMessages.InvalidMaxPriorityFeePerGas;
            return false;
        }

        return true;
    }
}

public sealed class ContractSizeTxValidator : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (transaction.IsAboveInitCode(releaseSpec))
        {
            error = TxErrorMessages.ContractSizeTooBig;
            return false;
        }

        return true;
    }
}

/// <summary>
///  Ensure that non Blob transactions do not contain Blob specific fields.
///  This validator will be deprecated once we have a proper Transaction type hierarchy.
/// </summary>
public sealed class NonBlobFieldsTxValidator : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        // Execution-payload version verification
        if (transaction.MaxFeePerBlobGas is not null)
        {
            error = TxErrorMessages.NotAllowedMaxFeePerBlobGas;
            return false;
        }

        if (transaction.BlobVersionedHashes is not null)
        {
            error = TxErrorMessages.NotAllowedBlobVersionedHashes;
            return false;
        }

        if (transaction is { NetworkWrapper: ShardBlobNetworkWrapper })
        {
            // NOTE: This must be an internal issue
            error = TxErrorMessages.InvalidTransaction;
            return false;
        }

        return true;
    }
}

public sealed class BlobFieldsTxValidator : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        if (transaction.To is null)
        {
            error = TxErrorMessages.TxMissingTo;
            return false;
        }

        if (transaction.MaxFeePerBlobGas is null)
        {
            error = TxErrorMessages.BlobTxMissingMaxFeePerBlobGas;
            return false;
        }

        if (transaction.BlobVersionedHashes is null)
        {
            error = TxErrorMessages.BlobTxMissingBlobVersionedHashes;
            return false;
        }

        var blobCount = transaction.BlobVersionedHashes.Length;
        var totalDataGas = BlobGasCalculator.CalculateBlobGas(blobCount);
        if (totalDataGas > Eip4844Constants.MaxBlobGasPerTransaction)
        {
            error = TxErrorMessages.BlobTxGasLimitExceeded;
            return false;
        }

        if (blobCount < Eip4844Constants.MinBlobsPerTransaction)
        {
            error = TxErrorMessages.BlobTxMissingBlobs;
            return false;
        }

        for (int i = 0; i < blobCount; i++)
        {
            if (transaction.BlobVersionedHashes[i] is null)
            {
                error = TxErrorMessages.MissingBlobVersionedHash;
                return false;
            }

            if (transaction.BlobVersionedHashes[i].Length != KzgPolynomialCommitments.BytesPerBlobVersionedHash)
            {
                error = TxErrorMessages.InvalidBlobVersionedHashSize;
                return false;
            }

            if (transaction.BlobVersionedHashes[i][0] != KzgPolynomialCommitments.KzgBlobHashVersionV1)
            {
                error = TxErrorMessages.InvalidBlobVersionedHashVersion;
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Validate Blob transactions in mempool version.
/// </summary>
public sealed class MempoolBlobTxValidator : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        if (transaction.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
        {
            return true;
        }

        int blobCount = transaction.BlobVersionedHashes.Length;
        if (wrapper.Blobs.Length != blobCount)
        {
            error = TxErrorMessages.InvalidBlobData;
            return false;
        }

        if (wrapper.Commitments.Length != blobCount)
        {
            error = TxErrorMessages.InvalidBlobData;
            return false;
        }

        if (wrapper.Proofs.Length != blobCount)
        {
            error = TxErrorMessages.InvalidBlobData;
            return false;
        }

        for (int i = 0; i < blobCount; i++)
        {
            if (wrapper.Blobs[i].Length != Ckzg.Ckzg.BytesPerBlob)
            {
                error = TxErrorMessages.ExceededBlobSize;
                return false;
            }
            if (wrapper.Commitments[i].Length != Ckzg.Ckzg.BytesPerCommitment)
            {
                error = TxErrorMessages.ExceededBlobCommitmentSize;
                return false;
            }
            if (wrapper.Proofs[i].Length != Ckzg.Ckzg.BytesPerProof)
            {
                error = TxErrorMessages.InvalidBlobProofSize;
                return false;
            }
        }

        Span<byte> hash = stackalloc byte[32];
        for (int i = 0; i < blobCount; i++)
        {
            if (!KzgPolynomialCommitments.TryComputeCommitmentHashV1(wrapper.Commitments[i].AsSpan(), hash) ||
                !hash.SequenceEqual(transaction.BlobVersionedHashes[i]))
            {
                error = TxErrorMessages.InvalidBlobCommitmentHash;
                return false;
            }
        }

        if (!KzgPolynomialCommitments.AreProofsValid(wrapper.Blobs, wrapper.Commitments, wrapper.Proofs))
        {
            error = TxErrorMessages.InvalidBlobProof;
            return false;
        }

        return true;
    }
}

public sealed class LegacySignatureTxValidator(ulong chainId) : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        Signature? signature = transaction.Signature;
        if (signature is null)
        {
            error = TxErrorMessages.InvalidTxSignature;
            return false;
        }

        UInt256 sValue = new(signature.SAsSpan, isBigEndian: true);
        UInt256 rValue = new(signature.RAsSpan, isBigEndian: true);

        if (sValue.IsZero || sValue >= (releaseSpec.IsEip2Enabled ? Secp256K1Curve.HalfNPlusOne : Secp256K1Curve.N))
        {
            error = TxErrorMessages.InvalidTxSignature;
            return false;
        }

        if (rValue.IsZero || rValue >= Secp256K1Curve.NMinusOne)
        {
            error = TxErrorMessages.InvalidTxSignature;
            return false;
        }

        if (signature.V is 27 or 28)
        {
            return true;
        }

        if (releaseSpec.IsEip155Enabled && (signature.V == chainId * 2 + 35ul || signature.V == chainId * 2 + 36ul))
        {
            return true;
        }

        if (releaseSpec.ValidateChainId)
        {
            error = TxErrorMessages.InvalidTxSignature;
            return false;
        }

        return true;
    }
}

public sealed class SignatureTxValidator : ITxValidator
{
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, [NotNullWhen(false)] out string? error)
    {
        error = null;

        Signature? signature = transaction.Signature;
        if (signature is null)
        {
            error = TxErrorMessages.InvalidTxSignature;
            return false;
        }

        UInt256 sValue = new(signature.SAsSpan, isBigEndian: true);
        UInt256 rValue = new(signature.RAsSpan, isBigEndian: true);

        if (sValue.IsZero || sValue >= (releaseSpec.IsEip2Enabled ? Secp256K1Curve.HalfNPlusOne : Secp256K1Curve.N))
        {
            error = TxErrorMessages.InvalidTxSignature;
            return false;
        }

        if (rValue.IsZero || rValue >= Secp256K1Curve.NMinusOne)
        {
            error = TxErrorMessages.InvalidTxSignature;
            return false;
        }

        if (signature.V is 27 or 28)
        {
            return true;
        }

        if (releaseSpec.ValidateChainId)
        {
            error = TxErrorMessages.InvalidTxSignature;
            return false;
        }

        return true;
    }
}
