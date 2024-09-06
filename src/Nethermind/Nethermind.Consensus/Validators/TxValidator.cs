// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using Nethermind.Consensus.Messages;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Consensus.Validators;

public sealed class TxValidator : ITxValidator
{
    private readonly ITxValidator[] _validators;

    public TxValidator(ulong chainId)
    {
        _validators = new ITxValidator[byte.MaxValue + 1];
        _validators[(byte)TxType.Legacy] = new CompositeTxValidator([
            IntrinsicGasTxValidator.Instance,
            new LegacySignatureTxValidator(chainId),
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
        ]);
        _validators[(byte)TxType.AccessList] = new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip2930Enabled),
            IntrinsicGasTxValidator.Instance,
            SignatureTxValidator.Instance,
            new ExpectedChainIdTxValidator(chainId),
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
        ]);
        _validators[(byte)TxType.EIP1559] = new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip1559Enabled),
            IntrinsicGasTxValidator.Instance,
            SignatureTxValidator.Instance,
            new ExpectedChainIdTxValidator(chainId),
            GasFieldsTxValidator.Instance,
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
        ]);
        _validators[(byte)TxType.Blob] = new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip4844Enabled),
            IntrinsicGasTxValidator.Instance,
            SignatureTxValidator.Instance,
            new ExpectedChainIdTxValidator(chainId),
            GasFieldsTxValidator.Instance,
            ContractSizeTxValidator.Instance,
            BlobFieldsTxValidator.Instance,
            MempoolBlobTxValidator.Instance
        ]);
    }

    public TxValidator WithValidator(TxType type, ITxValidator validator)
    {
        _validators[(byte)type] = validator;
        return this;
    }

    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) => IsWellFormed(transaction, releaseSpec, out _);

    /// <remarks>
    /// Full and correct validation is only possible in the context of a specific block
    /// as we cannot generalize correctness of the transaction without knowing the EIPs implemented
    /// and the world state(account nonce in particular).
    /// Even without protocol change, the tx can become invalid if another tx
    /// from the same account with the same nonce got included on the chain.
    /// As such, we can decide whether tx is well formed as long as we also validate nonce
    /// just before the execution of the block / tx.
    /// </remarks>
    public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, out string? error)
    {
        ITxValidator? validator = _validators[(byte)transaction.Type];
        if (validator is null)
        {
            error = TxErrorMessages.InvalidTxType(releaseSpec.Name);
            return false;
        }

        return validator.IsWellFormed(transaction, releaseSpec, out error);
    }
}

public sealed class CompositeTxValidator(List<ITxValidator> validators) : ITxValidator
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
    public static readonly IntrinsicGasTxValidator Instance = new();
    private IntrinsicGasTxValidator() { }

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
    public static readonly GasFieldsTxValidator Instance = new();
    private GasFieldsTxValidator() { }

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
    public static readonly ContractSizeTxValidator Instance = new();
    private ContractSizeTxValidator() { }

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

/// <remark>
///  Ensure that non Blob transactions do not contain Blob specific fields.
///  This validator will be deprecated once we have a proper Transaction type hierarchy.
/// </remark>
public sealed class NonBlobFieldsTxValidator : ITxValidator
{
    public static readonly NonBlobFieldsTxValidator Instance = new();
    private NonBlobFieldsTxValidator() { }

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
    public static readonly BlobFieldsTxValidator Instance = new();
    private BlobFieldsTxValidator() { }

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
    public static readonly MempoolBlobTxValidator Instance = new();
    private MempoolBlobTxValidator() { }

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

public abstract class BaseSignatureTxValidator : ITxValidator
{
    protected virtual bool ValidateChainId(Transaction transaction, IReleaseSpec releaseSpec) => false;

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

        if (ValidateChainId(transaction, releaseSpec))
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

public sealed class LegacySignatureTxValidator(ulong chainId) : BaseSignatureTxValidator
{
    protected override bool ValidateChainId(Transaction transaction, IReleaseSpec releaseSpec)
    {
        Signature signature = transaction.Signature;
        return releaseSpec.IsEip155Enabled && (signature.V == chainId * 2 + 35ul || signature.V == chainId * 2 + 36ul);
    }
}

public sealed class SignatureTxValidator : BaseSignatureTxValidator
{
    public static readonly SignatureTxValidator Instance = new();
    private SignatureTxValidator() { }
}
