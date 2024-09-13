// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class GasEstimator
    {
        /// <summary>
        /// Error margin used if none other is specified expressed in basis points.
        /// </summary>
        public const int DefaultErrorMargin = 150;

        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IReadOnlyStateProvider _stateProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IBlocksConfig _blocksConfig;

        public GasEstimator(ITransactionProcessor transactionProcessor, IReadOnlyStateProvider stateProvider,
            ISpecProvider specProvider, IBlocksConfig blocksConfig)
        {
            _transactionProcessor = transactionProcessor;
            _stateProvider = stateProvider;
            _specProvider = specProvider;
            _blocksConfig = blocksConfig;
        }

        public long Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, int errorMargin = DefaultErrorMargin, CancellationToken token = new())
        {
            ArgumentOutOfRangeException.ThrowIfNegative(errorMargin, nameof(errorMargin));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(errorMargin, 10000, nameof(errorMargin));
            IReleaseSpec releaseSpec = _specProvider.GetSpec(header.Number + 1, header.Timestamp + _blocksConfig.SecondsPerSlot);

            tx.SenderAddress ??= Address.Zero; // If sender is not specified, use zero address.
            tx.GasLimit = Math.Min(tx.GasLimit, header.GasLimit); // Limit Gas to the header

            // Calculate and return additional gas required in case of insufficient funds.
            UInt256 senderBalance = _stateProvider.GetBalance(tx.SenderAddress);
            if (tx.Value != UInt256.Zero && tx.Value > senderBalance)
            {
                return gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec);
            }

            long intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

            // Setting boundaries for binary search - determine lowest and highest gas can be used during the estimation:
            long leftBound = (gasTracer.GasSpent != 0 && gasTracer.GasSpent >= intrinsicGas)
                ? gasTracer.GasSpent - 1
                : intrinsicGas - 1;
            long rightBound = (tx.GasLimit != 0 && tx.GasLimit >= intrinsicGas)
                ? tx.GasLimit
                : header.GasLimit;

            //This would mean that header gas limit is lower than both intrinsic gas and tx gas limit
            if (leftBound > rightBound)
                return 0;

            // Execute binary search to find the optimal gas estimation.
            return BinarySearchEstimate(leftBound, rightBound, tx, header, gasTracer, errorMargin, token);
        }

        private long BinarySearchEstimate(long leftBound, long rightBound, Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, int errorMargin, CancellationToken token)
        {
            double marginWithDecimals = errorMargin == 0 ? 1 : errorMargin / 10000d + 1;
            //This approach is similar to Geth, by starting from an optimistic guess the number of iterations is greatly reduced in most cases
            long optimisticGasEstimate = (long)((gasTracer.GasSpent + gasTracer.TotalRefund + GasCostOf.CallStipend) * marginWithDecimals);
            if (optimisticGasEstimate > leftBound && optimisticGasEstimate < rightBound)
            {
                if (TryExecutableTransaction(tx, header, optimisticGasEstimate, token))
                    rightBound = optimisticGasEstimate;
                else
                    leftBound = optimisticGasEstimate;
            }

            long cap = rightBound;
            //This is similar to Geth's approach by stopping, when the estimation is within a certain margin of error
            while ((rightBound - leftBound) / (double)leftBound > (marginWithDecimals - 1)
                && leftBound + 1 < rightBound)
            {
                long mid = (leftBound + rightBound) / 2;
                if (!TryExecutableTransaction(tx, header, mid, token))
                {
                    leftBound = mid;
                }
                else
                {
                    rightBound = mid;
                }
            }

            if (rightBound == cap && !TryExecutableTransaction(tx, header, rightBound, token))
            {
                return 0;
            }

            return rightBound;
        }

        private bool TryExecutableTransaction(Transaction transaction, BlockHeader block, long gasLimit, CancellationToken token)
        {
            OutOfGasTracer tracer = new();

            // TODO: Workaround to not mutate the original Tx
            long originalGasLimit = transaction.GasLimit;

            transaction.GasLimit = gasLimit;

            BlockExecutionContext blCtx = new(block);
            _transactionProcessor.CallAndRestore(transaction, in blCtx, tracer.WithCancellation(token));
            transaction.GasLimit = originalGasLimit;

            return !tracer.OutOfGas;
        }

        private class OutOfGasTracer : TxTracer
        {
            public OutOfGasTracer()
            {
                OutOfGas = false;
            }
            public override bool IsTracingReceipt => true;
            public override bool IsTracingInstructions => true;
            public override bool IsTracingActions => true;
            public bool OutOfGas { get; private set; }

            public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
            {
            }

            public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Hash256? stateRoot = null)
            {
            }

            public override void ReportActionError(EvmExceptionType error)
            {
                OutOfGas |= error == EvmExceptionType.OutOfGas;
            }

            public override void ReportOperationError(EvmExceptionType error)
            {
                OutOfGas |= error == EvmExceptionType.OutOfGas;
            }
        }
    }
}
