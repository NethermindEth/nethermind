// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public sealed class OptimismLegacyTxDecoder : LegacyTxDecoder<Transaction>
{
    protected override Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (v == 0 && rBytes.IsEmpty && sBytes.IsEmpty)
        {
            return null;
        }
        return base.DecodeSignature(v, rBytes, sBytes, fallbackSignature, rlpBehaviors);
    }
}

public sealed class OptimismLegacyTxValidator(ulong chainId) : ITxValidator
{
    private readonly ITxValidator _postBedrockValidator = new CompositeTxValidator([
        new LegacySignatureTxValidator(chainId),
        ContractSizeTxValidator.Instance,
        NonBlobFieldsTxValidator.Instance,
        NonSetCodeFieldsTxValidator.Instance,
        GasLimitCapTxValidator.Instance,
        IntrinsicGasTxValidator.Instance
    ]);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
        => IsWellFormed(transaction, releaseSpec, blockGasLimit: 0);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit)
    {
        // In Optimism, EIP1559 is activated in Bedrock
        bool isPreBedrock = !releaseSpec.IsEip1559Enabled;
        if (isPreBedrock)
        {
            // Pre-Bedrock we perform no validation at all
            return ValidationResult.Success;
        }

        return _postBedrockValidator.IsWellFormed(transaction, releaseSpec, blockGasLimit);
    }
}

internal sealed class OptimismSpecChangeTxValidator : ITxValidator, ILightTxValidator, ISpecChangeTxValidator
{
    private readonly SpecChangeTxValidator _ethereumValidator;

    public OptimismSpecChangeTxValidator(ulong chainId)
    {
        _ethereumValidator = new(chainId);
        PersistenceFingerprint = FormattableString.Invariant(
            $"2|{typeof(OptimismSpecChangeTxValidator).Module.ModuleVersionId:N}|{_ethereumValidator.PersistenceFingerprint}");
    }

    public string PersistenceFingerprint { get; }

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.Type == TxType.Legacy && !releaseSpec.IsEip1559Enabled
            ? ValidationResult.Success
            : _ethereumValidator.IsWellFormed(transaction, releaseSpec);

    public ValidationResult IsWellFormedAfterFullValidation(Transaction transaction, IReleaseSpec releaseSpec) =>
        transaction.Type switch
        {
            TxType.Legacy when !releaseSpec.IsEip1559Enabled => ValidationResult.Success,
            TxType.DepositTx => _ethereumValidator.IsWellFormed(transaction, releaseSpec),
            _ => _ethereumValidator.IsWellFormedAfterFullValidation(transaction, releaseSpec)
        };

    public ValidationResult IsWellFormedLight(LightTransaction transaction, IReleaseSpec releaseSpec) =>
        _ethereumValidator.IsWellFormedLight(transaction, releaseSpec);
}
