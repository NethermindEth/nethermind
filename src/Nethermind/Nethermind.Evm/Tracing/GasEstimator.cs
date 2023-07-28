// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class GasEstimator
    {
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

        public long Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, CancellationToken cancellationToken = new())
        {
            IReleaseSpec releaseSpec = _specProvider.GetSpec(header.Number + 1, header.Timestamp + _blocksConfig.SecondsPerSlot);

            tx.GasLimit = Math.Min(tx.GasLimit, header.GasLimit); // Limit Gas to the header
            tx.SenderAddress ??= Address.Zero; //If sender is not specified, use zero address.

            // Setting boundaries for binary search - determine lowest and highest gas can be used during the estimation:
            long leftBound = (gasTracer.GasSpent != 0 && gasTracer.GasSpent >= Transaction.BaseTxGasCost)
                ? gasTracer.GasSpent - 1
                : Transaction.BaseTxGasCost - 1;
            long rightBound = (tx.GasLimit != 0 && tx.GasPrice >= Transaction.BaseTxGasCost)
                ? tx.GasLimit
                : header.GasLimit;

            UInt256 senderBalance = _stateProvider.GetBalance(tx.SenderAddress);

            // Calculate and return additional gas required in case of insufficient funds.
            if (tx.Value != UInt256.Zero && tx.Value >= senderBalance)
            {
                return gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec);
            }

            // Execute binary search to find the optimal gas estimation.
            try
            {
                return BinarySearchEstimate(leftBound, rightBound, tx, header, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        private long BinarySearchEstimate(long leftBound, long rightBound, Transaction tx, BlockHeader header, CancellationToken cancellationToken)
        {
            long cap = rightBound;

            while (leftBound + 1 < rightBound)
            {
                long mid = (leftBound + rightBound) / 2;
                if (!TryExecutableTransaction(tx, header, mid, cancellationToken))
                {
                    leftBound = mid;
                }
                else
                {
                    rightBound = mid;
                }
            }

            if (rightBound == cap && !TryExecutableTransaction(tx, header, rightBound, cancellationToken))
            {
                return 0;
            }

            return rightBound;
        }

        private bool TryExecutableTransaction(Transaction transaction, BlockHeader block, long gasLimit, CancellationToken cancellationToken)
        {
            OutOfGasTracer tracer = new();
            transaction.GasLimit = gasLimit;
            _transactionProcessor.CallAndRestore(transaction, block, tracer.WithCancellation(cancellationToken));

            return !tracer.OutOfGas;
        }

        private class OutOfGasTracer : TxTracer
        {
            public OutOfGasTracer()
            {
                OutOfGas = false;
            }

            public bool OutOfGas { get; private set; }

            public override void ReportOperationError(EvmExceptionType error)
            {
                OutOfGas |= error == EvmExceptionType.OutOfGas;
            }
        }
    }
}
