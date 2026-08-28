// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out malformed transactions and resolves the sender for subsequent state-dependent filters.
    /// </summary>
    internal sealed class MalformedTxFilter(
        ITxValidator txValidator,
        ITxValidator specChangeTxValidator,
        IEthereumEcdsa ecdsa,
        ILogger logger)
        : IIncomingTxFilter
    {
        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            IReleaseSpec spec = state.HeadSpec;
            ValidationResult result = Validate(tx, spec);
            bool retryAfterSenderRecovery = !result
                && spec.IsEip2780Enabled
                && tx.IsMessageCall
                && tx.SenderAddress is null
                && result.IsIntrinsicGasError
                && CanSenderRecoveryFixIntrinsicGas(tx, spec);
            if (!result && !retryAfterSenderRecovery)
            {
                return RejectMalformed(tx, result);
            }

            Metrics.PendingTransactionsWithExpensiveFiltering++;
            if (tx.SenderAddress is null)
            {
                if (!ecdsa.TryRecoverAddress(tx, out Address? senderAddress))
                {
                    Metrics.PendingTransactionsUnresolvableSender++;
                    if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, no sender.");
                    return AcceptTxResult.FailedToResolveSender;
                }
                tx.SenderAddress = senderAddress;
            }

            // An unresolved sender is conservatively priced as non-self, so only a rejected
            // intrinsic result can become valid after recovery.
            if (retryAfterSenderRecovery && !(result = Validate(tx, spec)))
            {
                return RejectMalformed(tx, result);
            }

            return AcceptTxResult.Accepted;

            ValidationResult Validate(Transaction transaction, IReleaseSpec releaseSpec)
            {
                ValidationResult validationResult = txValidator.IsWellFormed(
                    transaction,
                    releaseSpec,
                    blockGasLimit: 0,
                    TxValidationOptions.SkipBlobProofs);
                return validationResult
                    ? specChangeTxValidator is ISpecChangeTxValidator incrementalValidator
                        ? incrementalValidator.IsWellFormedAfterFullValidation(transaction, releaseSpec)
                        : specChangeTxValidator.IsWellFormed(transaction, releaseSpec)
                    : validationResult;
            }
        }

        private static bool CanSenderRecoveryFixIntrinsicGas(Transaction tx, IReleaseSpec spec)
        {
            IntrinsicGas<EthereumGasPolicy> selfTransferGas = EthereumGasPolicy.CalculateIntrinsicGasAsEip2780SelfTransfer(tx, spec);
            if (spec.IsEip8037Enabled && selfTransferGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap, out _, out _))
            {
                return false;
            }

            return tx.GasLimit >= selfTransferGas.MinRequiredGasLimit;
        }

        private AcceptTxResult RejectMalformed(Transaction tx, ValidationResult result)
        {
            Metrics.PendingTransactionsMalformed++;
            // It may happen that other nodes send us transactions that were signed for another chain or don't have enough gas.
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, invalid transaction: {result}");
            return AcceptTxResult.Invalid.WithMessage($"{result}");
        }
    }
}
