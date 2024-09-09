// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private readonly ITxValidator?[] _validators = new ITxValidator?[Transaction.MaxTxType + 1];

    public TxValidator(ulong chainId)
    {
        RegisterValidator(TxType.Legacy, new CompositeTxValidator([
            IntrinsicGasTxValidator.Instance,
            new LegacySignatureTxValidator(chainId),
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
        ]));
        RegisterValidator(TxType.AccessList, new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip2930Enabled),
            IntrinsicGasTxValidator.Instance,
            SignatureTxValidator.Instance,
            new ExpectedChainIdTxValidator(chainId),
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
        ]));
        RegisterValidator(TxType.EIP1559, new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip1559Enabled),
            IntrinsicGasTxValidator.Instance,
            SignatureTxValidator.Instance,
            new ExpectedChainIdTxValidator(chainId),
            GasFieldsTxValidator.Instance,
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
        ]));
        RegisterValidator(TxType.Blob, new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip4844Enabled),
            IntrinsicGasTxValidator.Instance,
            SignatureTxValidator.Instance,
            new ExpectedChainIdTxValidator(chainId),
            GasFieldsTxValidator.Instance,
            ContractSizeTxValidator.Instance,
            BlobFieldsTxValidator.Instance,
            MempoolBlobTxValidator.Instance
        ]));
    }

    public void RegisterValidator(TxType type, ITxValidator validator) => _validators[(byte)type] = validator;

    /// <remarks>
    /// Full and correct validation is only possible in the context of a specific block
    /// as we cannot generalize correctness of the transaction without knowing the EIPs implemented
    /// and the world state(account nonce in particular).
    /// Even without protocol change, the tx can become invalid if another tx
    /// from the same account with the same nonce got included on the chain.
    /// As such, we can decide whether tx is well formed as long as we also validate nonce
    /// just before the execution of the block / tx.
    /// </remarks>
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        ITxValidator? validator = _validators[(byte)transaction.Type];
        return validator?.IsWellFormed(transaction, releaseSpec) ?? TxErrorMessages.InvalidTxType(releaseSpec.Name);
    }
}

public sealed class CompositeTxValidator(List<ITxValidator> validators) : ITxValidator
{
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        foreach (ITxValidator validator in validators)
        {
            ValidationResult isWellFormed = validator.IsWellFormed(transaction, releaseSpec);
            if (!isWellFormed)
            {
                return isWellFormed;
            }
        }

        return ValidationResult.Success;
    }
}

public sealed class IntrinsicGasTxValidator : ITxValidator
{
    public static readonly IntrinsicGasTxValidator Instance = new();
    private IntrinsicGasTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        // This is unnecessarily calculated twice - at validation and execution times.
        transaction.GasLimit < IntrinsicGasCalculator.Calculate(transaction, releaseSpec)
            ? TxErrorMessages.IntrinsicGasTooLow
            : ValidationResult.Success;
}

public sealed class ReleaseSpecTxValidator(Func<IReleaseSpec, bool> validate) : ITxValidator
{
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        !validate(releaseSpec) ? TxErrorMessages.InvalidTxType(releaseSpec.Name) : ValidationResult.Success;
}

public sealed class ExpectedChainIdTxValidator(ulong chainId) : ITxValidator
{
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.ChainId != chainId ? TxErrorMessages.InvalidTxChainId(chainId, transaction.ChainId) : ValidationResult.Success;
}

public sealed class GasFieldsTxValidator : ITxValidator
{
    public static readonly GasFieldsTxValidator Instance = new();
    private GasFieldsTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.MaxFeePerGas < transaction.MaxPriorityFeePerGas ? TxErrorMessages.InvalidMaxPriorityFeePerGas : ValidationResult.Success;
}

public sealed class ContractSizeTxValidator : ITxValidator
{
    public static readonly ContractSizeTxValidator Instance = new();
    private ContractSizeTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.IsAboveInitCode(releaseSpec) ? TxErrorMessages.ContractSizeTooBig : ValidationResult.Success;
}

/// <remark>
///  Ensure that non Blob transactions do not contain Blob specific fields.
///  This validator will be deprecated once we have a proper Transaction type hierarchy.
/// </remark>
public sealed class NonBlobFieldsTxValidator : ITxValidator
{
    public static readonly NonBlobFieldsTxValidator Instance = new();
    private NonBlobFieldsTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) => transaction switch
    {
        // Execution-payload version verification
        { MaxFeePerBlobGas: not null } => TxErrorMessages.NotAllowedMaxFeePerBlobGas,
        { BlobVersionedHashes: not null } => TxErrorMessages.NotAllowedBlobVersionedHashes,
        { NetworkWrapper: ShardBlobNetworkWrapper } => TxErrorMessages.InvalidTransaction,
        _ => ValidationResult.Success
    };
}

public sealed class BlobFieldsTxValidator : ITxValidator
{
    public static readonly BlobFieldsTxValidator Instance = new();
    private BlobFieldsTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction switch
        {
            { To: null } => TxErrorMessages.TxMissingTo,
            { MaxFeePerBlobGas: null } => TxErrorMessages.BlobTxMissingMaxFeePerBlobGas,
            { BlobVersionedHashes: null } => TxErrorMessages.BlobTxMissingBlobVersionedHashes,
            _ => ValidateBlobFields(transaction)
        };

    private ValidationResult ValidateBlobFields(Transaction transaction)
    {
        int blobCount = transaction.BlobVersionedHashes!.Length;
        ulong totalDataGas = BlobGasCalculator.CalculateBlobGas(blobCount);
        return totalDataGas > Eip4844Constants.MaxBlobGasPerTransaction ? TxErrorMessages.BlobTxGasLimitExceeded
            : blobCount < Eip4844Constants.MinBlobsPerTransaction ? TxErrorMessages.BlobTxMissingBlobs
            : ValidateBlobVersionedHashes();

        ValidationResult ValidateBlobVersionedHashes()
        {
            for (int i = 0; i < blobCount; i++)
            {
                switch (transaction.BlobVersionedHashes[i])
                {
                    case null: return TxErrorMessages.MissingBlobVersionedHash;
                    case { Length: not KzgPolynomialCommitments.BytesPerBlobVersionedHash }: return TxErrorMessages.InvalidBlobVersionedHashSize;
                    case { Length: KzgPolynomialCommitments.BytesPerBlobVersionedHash } when transaction.BlobVersionedHashes[i][0] != KzgPolynomialCommitments.KzgBlobHashVersionV1: return TxErrorMessages.InvalidBlobVersionedHashVersion;
                }
            }

            return ValidationResult.Success;
        }
    }
}

/// <summary>
/// Validate Blob transactions in mempool version.
/// </summary>
public sealed class MempoolBlobTxValidator : ITxValidator
{
    public static readonly MempoolBlobTxValidator Instance = new();
    private MempoolBlobTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        int blobCount = transaction.BlobVersionedHashes!.Length;
        return transaction.NetworkWrapper is not ShardBlobNetworkWrapper wrapper ? ValidationResult.Success
            : wrapper.Blobs.Length != blobCount ? TxErrorMessages.InvalidBlobData
            : wrapper.Commitments.Length != blobCount ? TxErrorMessages.InvalidBlobData
            : wrapper.Proofs.Length != blobCount ? TxErrorMessages.InvalidBlobData
            : ValidateBlobs();

        ValidationResult ValidateBlobs()
        {
            for (int i = 0; i < blobCount; i++)
            {
                if (wrapper.Blobs[i].Length != Ckzg.Ckzg.BytesPerBlob)
                {
                    return TxErrorMessages.ExceededBlobSize;
                }

                if (wrapper.Commitments[i].Length != Ckzg.Ckzg.BytesPerCommitment)
                {
                    return TxErrorMessages.ExceededBlobCommitmentSize;
                }

                if (wrapper.Proofs[i].Length != Ckzg.Ckzg.BytesPerProof)
                {
                    return TxErrorMessages.InvalidBlobProofSize;
                }
            }

            Span<byte> hash = stackalloc byte[32];
            for (int i = 0; i < blobCount; i++)
            {
                if (!KzgPolynomialCommitments.TryComputeCommitmentHashV1(wrapper.Commitments[i].AsSpan(), hash) || !hash.SequenceEqual(transaction.BlobVersionedHashes[i]))
                {
                    return TxErrorMessages.InvalidBlobCommitmentHash;
                }
            }

            return !KzgPolynomialCommitments.AreProofsValid(wrapper.Blobs, wrapper.Commitments, wrapper.Proofs)
                ? TxErrorMessages.InvalidBlobProof
                : ValidationResult.Success;
        }
    }
}

public abstract class BaseSignatureTxValidator : ITxValidator
{
    protected virtual ValidationResult ValidateChainId(Transaction transaction, IReleaseSpec releaseSpec) =>
        releaseSpec.ValidateChainId ? TxErrorMessages.InvalidTxSignature : ValidationResult.Success;

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        Signature? signature = transaction.Signature;
        if (signature is null)
        {
            return TxErrorMessages.InvalidTxSignature;
        }

        UInt256 sValue = new(signature.SAsSpan, isBigEndian: true);
        UInt256 rValue = new(signature.RAsSpan, isBigEndian: true);

        return sValue.IsZero || sValue >= (releaseSpec.IsEip2Enabled ? Secp256K1Curve.HalfNPlusOne : Secp256K1Curve.N) ? TxErrorMessages.InvalidTxSignature
            : rValue.IsZero || rValue >= Secp256K1Curve.NMinusOne ? TxErrorMessages.InvalidTxSignature
            : signature.V is 27 or 28 ? ValidationResult.Success
            : ValidateChainId(transaction, releaseSpec);
    }
}

public sealed class LegacySignatureTxValidator(ulong chainId) : BaseSignatureTxValidator
{
    protected override ValidationResult ValidateChainId(Transaction transaction, IReleaseSpec releaseSpec)
    {
        ulong v = transaction.Signature!.V;
        return releaseSpec.IsEip155Enabled && (v == chainId * 2 + 35ul || v == chainId * 2 + 36ul)
            ? ValidationResult.Success
            : base.ValidateChainId(transaction, releaseSpec);
    }
}

public sealed class SignatureTxValidator : BaseSignatureTxValidator
{
    public static readonly SignatureTxValidator Instance = new();
    private SignatureTxValidator() { }
}
