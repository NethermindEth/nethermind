// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
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
        FormattableString.Invariant($"2|{typeof(SpecChangeTxValidator).Module.ModuleVersionId:N}|{typeof(EthereumGasPolicy).Module.ModuleVersionId:N}|{chainId}");

    /// <inheritdoc/>
    /// <remarks>
    /// This follows a successful <see cref="TxValidator"/> pass with blob proofs skipped. That pass already covers
    /// release activation, gas caps, proof version, contract size, signatures, and intrinsic gas. The inexpensive
    /// blob-count check is retained as the explicit fork-dependent admission guard. Any new rule added to this
    /// validator must also be added here unless the full validator applies it for every affected transaction type.
    /// </remarks>
    public ValidationResult IsWellFormedAfterFullValidation(Transaction transaction, IReleaseSpec releaseSpec) =>
        MaxBlobCountBlobTxValidator.Instance.IsWellFormed(transaction, releaseSpec);

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
