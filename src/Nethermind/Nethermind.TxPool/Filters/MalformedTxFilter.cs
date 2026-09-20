// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
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
        // Plugins may supply only ITxValidator, in which case they require the full validation pass.
        private readonly ISpecChangeTxValidator? _incrementalSpecChangeTxValidator =
            specChangeTxValidator as ISpecChangeTxValidator;

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
        {
            IReleaseSpec spec = state.HeadSpec;
            TxValidationOptions validationOptions = TxValidationOptions.SkipBlobProofs | TxValidationOptions.SkipIntrinsicGasMemo;
            if ((txHandlingOptions & TxHandlingOptions.PersistentBroadcast) == 0)
            {
                validationOptions |= TxValidationOptions.SkipErrorDetails;
            }
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
                    if (logger.IsTrace) TraceUnresolvableSender(tx);
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
                    validationOptions);
                return validationResult
                    ? _incrementalSpecChangeTxValidator is null
                        ? specChangeTxValidator.IsWellFormed(transaction, releaseSpec, blockGasLimit: 0,
                            validationOptions & ~TxValidationOptions.SkipBlobProofs)
                        : _incrementalSpecChangeTxValidator.IsWellFormedAfterFullValidation(transaction, releaseSpec)
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
            if (logger.IsTrace) TraceMalformed(tx, result);
            return AcceptTxResult.Invalid.WithMessage(result.ToString());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceUnresolvableSender(Transaction tx) =>
            logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, no sender.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TraceMalformed(Transaction tx, ValidationResult result) =>
            logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, invalid transaction: {result}");
    }
}
