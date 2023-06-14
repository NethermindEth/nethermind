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
            transaction.GasLimit = (long)gasLimit;
            _transactionProcessor.CallAndRestore(transaction, block, tracer);

            return !tracer.OutOfGas;
        }

        private class OutOfGasTracer : ITxTracer
        {
            public OutOfGasTracer()
            {
                OutOfGas = false;
            }
            public bool IsTracingReceipt => true;
            public bool IsTracingActions => false;
            public bool IsTracingOpLevelStorage => false;
            public bool IsTracingMemory => false;
            public bool IsTracingInstructions => true;
            public bool IsTracingRefunds => false;
            public bool IsTracingCode => false;
            public bool IsTracingStack => false;
            public bool IsTracingState => false;
            public bool IsTracingStorage => false;
            public bool IsTracingBlockHash => false;
            public bool IsTracingAccess => false;
            public bool IsTracingFees => false;
            public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees;

            public bool OutOfGas { get; set; }

            public byte[] ReturnValue { get; set; }

            public byte StatusCode { get; set; }

            public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
            {
                ReturnValue = output;
                StatusCode = Evm.StatusCode.Success;
            }

            public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
            {
                ReturnValue = output ?? Array.Empty<byte>();
                StatusCode = Evm.StatusCode.Failure;
            }

            public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
            {
            }

            public void ReportOperationError(EvmExceptionType error)
            {
                OutOfGas |= error == EvmExceptionType.OutOfGas;
            }

            public void ReportOperationRemainingGas(long gas)
            {
            }

            public void SetOperationMemorySize(ulong newSize)
            {
            }

            public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
            {
            }

            public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
            {
            }

            public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
            {
            }

            public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
            {
            }

            public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
            {
                throw new NotSupportedException();
            }

            public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
            {
                throw new NotSupportedException();
            }

            public void ReportCodeChange(Address address, byte[] before, byte[] after)
            {
                throw new NotSupportedException();
            }

            public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
            {
                throw new NotSupportedException();
            }

            public void ReportAccountRead(Address address)
            {
            }

            public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
            {
                throw new NotSupportedException();
            }

            public void ReportStorageRead(in StorageCell storageCell)
            {
                throw new NotSupportedException();
            }

            public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
            {
                throw new NotSupportedException();
            }

            public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
            {
                throw new NotSupportedException();
            }

            public void ReportActionError(EvmExceptionType exceptionType)
            {
                throw new NotSupportedException();
            }

            public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
            {
                throw new NotSupportedException();
            }

            public void ReportBlockHash(Keccak blockHash)
            {
                throw new NotSupportedException();
            }

            public void ReportByteCode(byte[] byteCode)
            {
                throw new NotSupportedException();
            }

            public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
            {
            }

            public void ReportRefund(long refund)
            {
                throw new NotSupportedException();
            }

            public void ReportExtraGasPressure(long extraGasPressure)
            {
                throw new NotSupportedException();
            }

            public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
            {
                throw new NotSupportedException();
            }

            public void SetOperationStack(List<string> stackTrace)
            {
            }

            public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
            {
            }

            public void SetOperationMemory(List<string> memoryTrace)
            {
            }

            public void ReportFees(UInt256 fees, UInt256 burntFees)
            {
                throw new NotImplementedException();
            }
        }
    }
}
