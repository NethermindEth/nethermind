// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

public class XdcBlockValidator(
    ITxValidator txValidator,
    IHeaderValidator headerValidator,
    IUnclesValidator unclesValidator,
    ISpecProvider specProvider,
    ILogManager logManager) : BlockValidator(
        new BlockTransactionValidator(txValidator), headerValidator, unclesValidator, specProvider, logManager)
{
    protected override bool ValidateEip4844Fields(Block block, IReleaseSpec spec, ref string? error) => true;

    private sealed class BlockTransactionValidator(ITxValidator inner) : ITxValidator
    {
        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec spec) =>
            IsWellFormed(transaction, spec, 0);

        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec spec, ulong blockGasLimit) =>
            IsWellFormed(transaction, spec, blockGasLimit, TxValidationOptions.None);

        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec spec, ulong blockGasLimit, TxValidationOptions options)
        {
            // The XDC processor applies these transactions outside the EVM with zero intrinsic gas.
            if (transaction.RequiresSpecialHandling((IXdcReleaseSpec)spec))
            {
                options |= TxValidationOptions.SkipIntrinsicGas;
            }

            return inner.IsWellFormed(transaction, spec, blockGasLimit, options);
        }
    }
}
