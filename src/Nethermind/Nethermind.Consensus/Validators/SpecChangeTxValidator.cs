// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Validation;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// Validates transaction properties whose validity can change when the release specification changes.
/// </summary>
public sealed class SpecChangeTxValidator() :
    CompositeTxValidator(
        TxTypeSpecValidator.Instance,
        MaxBlobCountBlobTxValidator.Instance,
        GasLimitCapTxValidator.Instance,
        MempoolBlobTxProofVersionValidator.Instance,
        IntrinsicGasTxValidator.Instance)
{
    public static readonly SpecChangeTxValidator Instance = new();

    private sealed class TxTypeSpecValidator : ITxValidator
    {
        public static readonly TxTypeSpecValidator Instance = new();

        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
            IsSupported(transaction.Type, releaseSpec)
                ? ValidationResult.Success
                : TxErrorMessages.InvalidTxType(releaseSpec.Name);

        private static bool IsSupported(TxType type, IReleaseSpec releaseSpec) => type switch
        {
            TxType.Legacy => true,
            TxType.AccessList => releaseSpec.IsEip2930Enabled,
            TxType.EIP1559 => releaseSpec.IsEip1559Enabled,
            TxType.Blob => releaseSpec.IsEip4844Enabled,
            TxType.SetCode => releaseSpec.IsEip7702Enabled,
            _ => false,
        };
    }
}
