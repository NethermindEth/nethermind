// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.TxPool
{
    [System.Flags]
    public enum TxValidationOptions
    {
        None = 0,
        SkipBlobProofs = 1,
    }

    public interface ITxValidator
    {
        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec);
        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit) =>
            IsWellFormed(transaction, releaseSpec);
        public ValidationResult IsWellFormed(
            Transaction transaction,
            IReleaseSpec releaseSpec,
            ulong blockGasLimit,
            TxValidationOptions options) => IsWellFormed(transaction, releaseSpec, blockGasLimit);

        public const string HeadTxValidatorKey = "HeadTxValidator";
    }
}
