// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Validation;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Validators;

public sealed class TxValidator : ITxValidator
{
    private readonly ITxValidator?[] _validators = new ITxValidator?[Transaction.MaxTxType + 1];

    public TxValidator(ulong chainId)
    {
        // Sender-independent failures must precede intrinsic gas so malformed transactions cannot
        // trigger EIP-2780 sender recovery; expensive blob proof validation remains after the gas gate.
        RegisterValidator(TxType.Legacy, new CompositeTxValidator([
            NonceCapTxValidator.Instance,
            new LegacySignatureTxValidator(chainId),
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
            NonSetCodeFieldsTxValidator.Instance,
            GasLimitCapTxValidator.Instance,
            IntrinsicGasTxValidator.Instance
        ]));

        ExpectedChainIdTxValidator expectedChainIdTxValidator = new(chainId);
        RegisterValidator(TxType.AccessList, new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip2930Enabled),
            NonceCapTxValidator.Instance,
            SignatureTxValidator.Instance,
            expectedChainIdTxValidator,
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
            NonSetCodeFieldsTxValidator.Instance,
            GasLimitCapTxValidator.Instance,
            IntrinsicGasTxValidator.Instance
        ]));
        RegisterValidator(TxType.EIP1559, new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip1559Enabled),
            NonceCapTxValidator.Instance,
            SignatureTxValidator.Instance,
            expectedChainIdTxValidator,
            GasFieldsTxValidator.Instance,
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
            NonSetCodeFieldsTxValidator.Instance,
            GasLimitCapTxValidator.Instance,
            IntrinsicGasTxValidator.Instance
        ]));
        RegisterValidator(TxType.Blob, new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip4844Enabled),
            NonceCapTxValidator.Instance,
            SignatureTxValidator.Instance,
            expectedChainIdTxValidator,
            GasFieldsTxValidator.Instance,
            ContractSizeTxValidator.Instance,
            BlobFieldsTxValidator.Instance,
            MempoolBlobTxProofVersionValidator.Instance,
            NonSetCodeFieldsTxValidator.Instance,
            GasLimitCapTxValidator.Instance,
            IntrinsicGasTxValidator.Instance,
            MempoolBlobTxValidator.Instance
        ]));
        RegisterValidator(TxType.SetCode, new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip7702Enabled),
            NonceCapTxValidator.Instance,
            SignatureTxValidator.Instance,
            expectedChainIdTxValidator,
            GasFieldsTxValidator.Instance,
            ContractSizeTxValidator.Instance,
            NonBlobFieldsTxValidator.Instance,
            NoContractCreationTxValidator.Instance,
            AuthorizationListTxValidator.Instance,
            GasLimitCapTxValidator.Instance,
            IntrinsicGasTxValidator.Instance
        ]));
        // Frame transactions have no envelope ECDSA signature (explicit sender, protocol-validated
        // signature list) — signature/intrinsic-gas validators do not apply; per-frame gas and
        // signature validation happen during processing.
        RegisterValidator(TxType.FrameTx, new CompositeTxValidator([
            new ReleaseSpecTxValidator(static spec => spec.IsEip8141Enabled),
            NonceCapTxValidator.Instance,
            expectedChainIdTxValidator,
            GasFieldsTxValidator.Instance,
            FrameTxFieldsTxValidator.Instance,
            FrameTxNonceKeysTxValidator.Instance,
            FrameTxEnvelopeTxValidator.Instance,
            FrameTxPostTxModeValidator.Instance,
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
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        IsWellFormed(transaction, releaseSpec, blockGasLimit: 0);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit) =>
        _validators.TryGetByTxType(transaction.Type, out ITxValidator validator)
            ? validator.IsWellFormed(transaction, releaseSpec, blockGasLimit)
            : TxErrorMessages.InvalidTxType(releaseSpec.Name);
}

public class CompositeTxValidator(params ITxValidator[] validators) : ITxValidator
{
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
        => IsWellFormed(transaction, releaseSpec, blockGasLimit: 0);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit)
    {
        foreach (ITxValidator validator in validators)
        {
            ValidationResult isWellFormed = validator.IsWellFormed(transaction, releaseSpec, blockGasLimit);
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

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
        => IsWellFormed(transaction, releaseSpec, blockGasLimit: 0);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit)
    {
        IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(transaction, releaseSpec, blockGasLimit);
        if (releaseSpec.IsEip8037Enabled && intrinsicGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap, out ulong regular, out ulong floor))
        {
            return IntrinsicGasError(TxErrorMessages.TxIntrinsicGasExceedsCap(regular, floor, Eip7825Constants.DefaultTxGasLimitCap));
        }

        return transaction.GasLimit < intrinsicGas.MinRequiredGasLimit
            ? IntrinsicGasError(TxErrorMessages.IntrinsicGasTooLow)
            : ValidationResult.Success;
    }

    private static ValidationResult IntrinsicGasError(string error) => new(error) { IsIntrinsicGasError = true };
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

/// <summary>
/// EIP-8141 static constraints (frame modes, flags, atomic batch shape, signature schemes).
/// </summary>
public sealed class FrameTxFieldsTxValidator : ITxValidator
{
    public static readonly FrameTxFieldsTxValidator Instance = new();
    private FrameTxFieldsTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        FrameTxValidation.IsWellFormed(transaction, out string? error) ? ValidationResult.Success : error!;
}

/// <summary>Admits the EIP-8250 keyed-nonce envelope only on forks that define it, and only well-formed.</summary>
/// <remarks>
/// Pre-fork the keys carry no replay protection at all — the account nonce is left untouched — so admitting
/// one would make the transaction replayable. Well-formedness is re-checked because <c>eth_call</c>,
/// <c>eth_estimateGas</c> and block building construct a transaction without going through the decoder.
/// </remarks>
public sealed class FrameTxNonceKeysTxValidator : ITxValidator
{
    public static readonly FrameTxNonceKeysTxValidator Instance = new();
    private FrameTxNonceKeysTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        UInt256[]? nonceKeys = transaction.NonceKeys;
        if (!releaseSpec.IsEip8250Enabled)
        {
            return nonceKeys is null ? ValidationResult.Success : "keyed nonces are not enabled";
        }

        if (nonceKeys is null)
        {
            return "frame transaction must carry a nonce key set";
        }

        return KeyedNonceManager.AreNonceKeysWellFormed(nonceKeys) && transaction.Nonce < Eip8250Constants.MaxNonceSeq
            ? ValidationResult.Success
            : "malformed nonce key set";
    }
}

/// <summary>Admits the frame-transaction envelope extensions only on forks that define them.</summary>
/// <remarks>
/// The RLP decoder tells the envelope shapes apart without fork context, so the fork that admits each
/// one is decided here. The reference cap is re-checked because a transaction can reach validation
/// without passing through the decoder at all — <c>eth_call</c>, <c>eth_estimateGas</c> and block
/// building all construct one directly.
/// </remarks>
public sealed class FrameTxEnvelopeTxValidator : ITxValidator
{
    public static readonly FrameTxEnvelopeTxValidator Instance = new();
    private FrameTxEnvelopeTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        RecentRootReference[]? references = transaction.RecentRootReferences;
        if (!releaseSpec.IsEip8272Enabled)
        {
            return references is null ? ValidationResult.Success : "recent root references are not enabled";
        }

        return references is null
            ? "frame transaction must carry a recent root reference list"
            : references.Length <= Eip8272Constants.MaxRecentRootReferences
                ? ValidationResult.Success
                : "too many recent root references";
    }
}

/// <summary>Admits the EIP-7906 <c>POST_TX</c> frame mode only on forks that define it.</summary>
/// <remarks>
/// The frame-list shape check accepts the mode without fork context. Before the fork the mode is
/// undefined, and running such a frame as <c>DEFAULT</c> would give it the write access the assertion
/// semantics forbid.
/// </remarks>
public sealed class FrameTxPostTxModeValidator : ITxValidator
{
    public static readonly FrameTxPostTxModeValidator Instance = new();
    private FrameTxPostTxModeValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        if (releaseSpec.IsEip7906Enabled || transaction.Frames is not { } frames)
        {
            return ValidationResult.Success;
        }

        foreach (TxFrame frame in frames)
        {
            if (frame.Mode == TxFrame.ModePostTx)
            {
                return "POST_TX frames are not enabled";
            }
        }

        return ValidationResult.Success;
    }
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
        { NetworkWrapper: ShardBlobNetworkWrapper } => TxErrorMessages.InvalidTransactionForm,
        _ => ValidationResult.Success
    };
}

public sealed class NonSetCodeFieldsTxValidator : ITxValidator
{
    public static readonly NonSetCodeFieldsTxValidator Instance = new();
    private NonSetCodeFieldsTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) => transaction switch
    {
        { AuthorizationList: not null } => TxErrorMessages.NotAllowedAuthorizationList,
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
            _ => ValidateBlobFields(transaction, releaseSpec)
        };

    private static ValidationResult ValidateBlobFields(Transaction transaction, IReleaseSpec spec)
    {
        int blobCount = transaction.BlobVersionedHashes!.Length;

        ValidationResult blobPerTxLimitValidationResult = ValidateBlobGasLimits(blobCount, spec);

        if (!blobPerTxLimitValidationResult)
        {
            return blobPerTxLimitValidationResult;
        }

        for (int i = 0; i < blobCount; i++)
        {
            switch (transaction.BlobVersionedHashes[i])
            {
                case null: return TxErrorMessages.MissingBlobVersionedHash;
                case { Length: not Eip4844Constants.BytesPerBlobVersionedHash }: return TxErrorMessages.InvalidBlobVersionedHashSize;
                case { Length: Eip4844Constants.BytesPerBlobVersionedHash } when transaction.BlobVersionedHashes[i][0] != KzgPolynomialCommitments.KzgBlobHashVersionV1: return TxErrorMessages.InvalidBlobVersionedHashVersion;
            }
        }

        return ValidationResult.Success;
    }

    public static ValidationResult ValidateBlobGasLimits(int txBlobCount, IReleaseSpec spec)
    {
        if (txBlobCount < Eip4844Constants.MinBlobsPerTransaction)
        {
            return TxErrorMessages.BlobTxMissingBlobs;
        }

        ulong txBlobGas = BlobGasCalculator.CalculateBlobGas(txBlobCount);

        ulong maxBlobGasPerBlock = spec.GasCosts.MaxBlobGasPerBlock;

        if (txBlobGas > maxBlobGasPerBlock)
        {
            return BlockErrorMessages.BlobGasUsedAboveBlockLimit(maxBlobGasPerBlock, txBlobCount, txBlobGas);
        }

        ulong maxBlobGasPerTx = spec.GasCosts.MaxBlobGasPerTx;

        return txBlobGas > maxBlobGasPerTx ? TxErrorMessages.BlobTxGasLimitExceeded(txBlobGas, maxBlobGasPerTx) : ValidationResult.Success;
    }
}

public sealed class MaxBlobCountBlobTxValidator : ITxValidator
{
    public static readonly MaxBlobCountBlobTxValidator Instance = new();
    private MaxBlobCountBlobTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction switch
        {
            { Type: not TxType.Blob } => ValidationResult.Success,
            _ => ValidateBlobFields(transaction, releaseSpec)
        };

    private static ValidationResult ValidateBlobFields(Transaction transaction, IReleaseSpec spec) =>
        BlobFieldsTxValidator.ValidateBlobGasLimits(transaction.BlobVersionedHashes?.Length ?? 0, spec);
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
        return transaction switch
        {
            { NetworkWrapper: null } => ValidationResult.Success,
            { Type: TxType.Blob, NetworkWrapper: ShardBlobNetworkWrapper wrapper } => ValidateBlobs(transaction, wrapper),
            { Type: TxType.Blob } or { NetworkWrapper: not null } => TxErrorMessages.InvalidTransactionForm,
        };

        static ValidationResult ValidateBlobs(Transaction transaction, ShardBlobNetworkWrapper wrapper)
        {
            IBlobProofsVerifier proofsManager = IBlobProofsManager.For(wrapper.Version);

            return (transaction.BlobVersionedHashes?.Length ?? 0) != wrapper.Blobs.Length || !proofsManager.ValidateLengths(wrapper) ? TxErrorMessages.InvalidBlobDataSize :
                transaction.BlobVersionedHashes is null || !proofsManager.ValidateHashes(wrapper, transaction.BlobVersionedHashes) ? TxErrorMessages.InvalidBlobHashes :
                !proofsManager.ValidateProofs(wrapper) ? TxErrorMessages.InvalidBlobProofs :
                ValidationResult.Success;
        }
    }
}

/// <summary>
/// Validate tx proof version in mempool version.
/// </summary>
public sealed class MempoolBlobTxProofVersionValidator : ITxValidator
{
    public static readonly MempoolBlobTxProofVersionValidator Instance = new();
    private MempoolBlobTxProofVersionValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        if (!transaction.SupportsBlobs) return ValidationResult.Success;

        ProofVersion? version = transaction.GetProofVersion();
        return version is null
            ? transaction.NetworkWrapper is not null ? TxErrorMessages.InvalidTransactionForm : ValidationResult.Success
            : ValidateProofVersion(version.Value, releaseSpec);

        static ValidationResult ValidateProofVersion(ProofVersion txProofVersion, IReleaseSpec spec) =>
            txProofVersion != spec.BlobProofVersion ? TxErrorMessages.InvalidProofVersion : ValidationResult.Success;
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

        UInt256 sMax = releaseSpec.IsEip2Enabled ? SecP256k1Curve.HalfNPlusOne : SecP256k1Curve.N;
        return sValue.IsZero || sValue >= sMax ? TxErrorMessages.InvalidTxSignature
            : rValue.IsZero || rValue >= SecP256k1Curve.N ? TxErrorMessages.InvalidTxSignature
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

public sealed class NoContractCreationTxValidator : ITxValidator
{
    public static readonly NoContractCreationTxValidator Instance = new();
    private NoContractCreationTxValidator() { }
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        SetCodeTxValidation.ValidateNoContractCreation(transaction);
}

public sealed class AuthorizationListTxValidator : ITxValidator
{
    public static readonly AuthorizationListTxValidator Instance = new();
    private AuthorizationListTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        SetCodeTxValidation.ValidateAuthorizationList(transaction);
}

public sealed class GasLimitCapTxValidator : ITxValidator
{
    public static readonly GasLimitCapTxValidator Instance = new();
    private GasLimitCapTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        ulong gasLimitCap = releaseSpec.GetTxGasLimitCap();
        return transaction.GasLimit > gasLimitCap ?
            TxErrorMessages.TxGasLimitCapExceeded(transaction.GasLimit, gasLimitCap) : ValidationResult.Success;
    }
}

/// <summary>
/// EIP-2681 validation.
/// </summary>
public sealed class NonceCapTxValidator : ITxValidator
{
    public static readonly NonceCapTxValidator Instance = new();
    private NonceCapTxValidator() { }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.Nonce < ulong.MaxValue ? ValidationResult.Success : TxErrorMessages.NonceTooHigh;
}
