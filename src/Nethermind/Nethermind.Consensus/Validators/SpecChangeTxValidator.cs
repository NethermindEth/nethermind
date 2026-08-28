// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// Validates transaction properties whose validity can change when the release specification changes.
/// </summary>
public sealed class SpecChangeTxValidator(ulong chainId) :
    CompositeTxValidator([
        .. HeadTxValidator.Validators,
        ContractSizeTxValidator.Instance,
        new SpecChangeSignatureTxValidator(chainId),
        IntrinsicGasTxValidator.Instance
    ]), ILightTxValidator, ISpecChangeTxValidator
{
    private static readonly HeadTxValidator LightTxValidator = new();

    public string PersistenceFingerprint { get; } =
        FormattableString.Invariant($"1|{typeof(SpecChangeTxValidator).Module.ModuleVersionId:N}|{chainId}");

    public ValidationResult IsWellFormedLight(LightTransaction transaction, IReleaseSpec releaseSpec) =>
        LightTxValidator.IsWellFormed(transaction, releaseSpec);

    private sealed class SpecChangeSignatureTxValidator(ulong chainId) : ITxValidator
    {
        private readonly LegacySignatureTxValidator _legacyValidator = new(chainId);

        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) => transaction.Type switch
        {
            TxType.Legacy => _legacyValidator.IsWellFormed(transaction, releaseSpec),
            TxType.AccessList or TxType.EIP1559 or TxType.Blob or TxType.SetCode =>
                SignatureTxValidator.Instance.IsWellFormed(transaction, releaseSpec),
            _ => ValidationResult.Success,
        };
    }
}
