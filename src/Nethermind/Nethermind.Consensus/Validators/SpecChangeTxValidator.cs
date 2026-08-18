// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// Validates transaction properties whose validity can change when the release specification changes.
/// </summary>
public sealed class SpecChangeTxValidator : ITxValidator
{
    public static readonly SpecChangeTxValidator Instance = new();

    private static readonly ITxValidator HeadValidator = new HeadTxValidator();

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
        => IsWellFormed(transaction, releaseSpec, blockGasLimit: 0);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit)
    {
        ValidationResult headValidation = HeadValidator.IsWellFormed(transaction, releaseSpec, blockGasLimit);
        if (!headValidation || transaction is LightTransaction)
        {
            return headValidation;
        }

        return IntrinsicGasTxValidator.Instance.IsWellFormed(transaction, releaseSpec, blockGasLimit);
    }
}
