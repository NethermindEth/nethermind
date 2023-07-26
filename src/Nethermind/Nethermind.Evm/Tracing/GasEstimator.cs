// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        public long Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer)
        {
            IReleaseSpec releaseSpec = _specProvider.GetSpec(header.Number + 1, header.Timestamp + _blocksConfig.SecondsPerSlot);

            long intrinsicGas = tx.GasLimit - gasTracer.IntrinsicGasAt;
            if (tx.GasLimit > header.GasLimit)
            {
                return Math.Max(intrinsicGas, gasTracer.GasSpent + gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec));
            }

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
            return BinarySearchEstimate(leftBound, rightBound, rightBound, tx, header);
        }

        private long BinarySearchEstimate(long leftBound, long rightBound, long cap, Transaction tx, BlockHeader header)
        {
            while (leftBound + 1 < rightBound)
            {
                long mid = (leftBound + rightBound) / 2;
                if (!TryExecutableTransaction(tx, header, mid))
                {
                    leftBound = mid;
                }
                else
                {
                    rightBound = mid;
                }
            }

            if (rightBound == cap && !TryExecutableTransaction(tx, header, rightBound))
            {
                return 0;
            }

            return rightBound;
        }

        private bool TryExecutableTransaction(Transaction transaction, BlockHeader block, long gasLimit)
        {
            OutOfGasTracer tracer = new();
            transaction.GasLimit = gasLimit;
            _transactionProcessor.CallAndRestore(transaction, block, tracer);

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
            public bool OutOfGas { get; set; }

            public byte[]? ReturnValue { get; set; }

            public byte StatusCode { get; set; }

            public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
            {
                ReturnValue = output;
                StatusCode = Evm.StatusCode.Success;
            }

            public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
            {
                ReturnValue = output;
                StatusCode = Evm.StatusCode.Failure;
            }

            public override void ReportOperationError(EvmExceptionType error)
            {
                OutOfGas |= error == EvmExceptionType.OutOfGas;
            }
        }
    }
}
