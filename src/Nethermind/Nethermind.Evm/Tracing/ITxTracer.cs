// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public interface ITxTracer : IWorldStateTracer, IDisposable
{
    bool IsCancelable => false;
    bool IsCancelled => false;
    /// <summary>
    /// Defines whether MarkAsSuccess or MarkAsFailed will be called
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="MarkAsSuccess"/>
    /// - <see cref="MarkAsFailed"/>
    /// </remarks>
    bool IsTracingReceipt { get; }

    /// <summary>
    /// High level calls with information on the target account
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportSelfDestruct"/>
    /// - <see cref="ReportAction"/>
    /// - <see cref="ReportActionEnd"/>
    /// - <see cref="ReportActionError"/>
    /// </remarks>
    bool IsTracingActions { get; }

    /// <summary>
    /// SSTORE and SLOAD level storage operations
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="SetOperationStorage"/>
    /// - <see cref="LoadOperationStorage"/>
    /// - <see cref="LoadOperationTransientStorage"/>
    /// - <see cref="SetOperationTransientStorage"/>
    /// </remarks>
    bool IsTracingOpLevelStorage { get; }

    /// <summary>
    /// EVM memory access operations
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="SetOperationMemory"/>
    /// - <see cref="SetOperationMemorySize"/>
    /// </remarks>
    bool IsTracingMemory { get; }

    /// <summary>
    /// EVM instructions
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="StartOperation"/>
    /// - <see cref="ReportOperationError"/>
    /// - <see cref="ReportOperationRemainingGas"/>
    /// - <see cref="ReportStackPush"/>
    /// - <see cref="ReportMemoryChange"/>
    /// - <see cref="ReportGasUpdateForVmTrace"/>
    /// </remarks>
    bool IsTracingInstructions { get; }

    /// <summary>
    /// Updates of refund counter
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportRefund"/>
    /// - <see cref="ReportExtraGasPressure"/>
    /// </remarks>
    bool IsTracingRefunds { get; }

    /// <summary>
    /// Code deployment
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportByteCode"/>
    /// </remarks>
    bool IsTracingCode { get; }

    /// <summary>
    /// EVM stack tracing after each operation
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="SetOperationStack"/>
    /// </remarks>
    bool IsTracingStack { get; }

    /// <summary>
    /// Traces blockhash calls
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportBlockHash"/>
    /// </remarks>
    bool IsTracingBlockHash { get; }

    /// <summary>
    /// Traces storage access
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportAccess"/>
    /// </remarks>
    bool IsTracingAccess { get; }

    /// <summary>
    /// Traces fees and burned fees
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportFees"/>
    /// </remarks>
    bool IsTracingFees { get; }

    /// <summary>
    /// Traces operation logs
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportLog"/>
    /// </remarks>
    bool IsTracingLogs { get; }

    bool IsTracing => IsTracingReceipt
                      || IsTracingActions
                      || IsTracingOpLevelStorage
                      || IsTracingMemory
                      || IsTracingInstructions
                      || IsTracingRefunds
                      || IsTracingCode
                      || IsTracingStack
                      || IsTracingBlockHash
                      || IsTracingAccess
                      || IsTracingFees
                      || IsTracingLogs;

    /// <summary>
    /// Transaction completed successfully
    /// </summary>
    /// <param name="recipient">Transaction recipient</param>
    /// <param name="gasSpent">Gas spent on transaction execution</param>
    /// <param name="output">Output of transaction</param>
    /// <param name="logs">Logs for transaction</param>
    /// <param name="stateRoot">State root after transaction, depends on EIP-658</param>
    /// <remarks>Depends on <see cref="IsTracingReceipt"/></remarks>
    void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null);

    /// <summary>
    /// Transaction failed
    /// </summary>
    /// <param name="recipient">Transaction recipient</param>
    /// <param name="gasSpent">Gas spent on transaction execution</param>
    /// <param name="output">Output of transaction</param>
    /// <param name="error">Error that failed the transaction</param>
    /// <param name="stateRoot">State root after transaction, depends on EIP-658</param>
    /// <remarks>Depends on <see cref="IsTracingReceipt"/></remarks>
    void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null);

    /// <summary>
    ///
    /// </summary>
    /// <param name="pc"></param>
    /// <param name="opcode"></param>
    /// <param name="gas"></param>
    /// <param name="env"></param>
    /// <param name="codeSection"></param>
    /// <param name="functionDepth"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0);

    /// <summary>
    ///
    /// </summary>
    /// <param name="error"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportOperationError(EvmExceptionType error);

    /// <summary>
    ///
    /// </summary>
    /// <param name="gas"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportOperationRemainingGas(long gas);


    /// <summary>
    ///
    /// </summary>
    /// <param name="log"></param>
    /// <remarks>Depends on <see cref="IsTracingLogs"/></remarks>
    void ReportLog(LogEntry log);

    /// <summary>
    ///
    /// </summary>
    /// <param name="stack"></param>
    /// <remarks>Depends on <see cref="IsTracingStack"/></remarks>
    void SetOperationStack(TraceStack stack);

    /// <summary>
    ///
    /// </summary>
    /// <param name="stackItem"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportStackPush(in ReadOnlySpan<byte> stackItem);

    /// <summary>
    /// </summary>
    /// <param name="stackItem"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportStackPush(byte stackItem)
    {
        ReportStackPush(new[] { stackItem });
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="stackItem"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportStackPush(in ZeroPaddedSpan stackItem)
    {
        ReportStackPush(stackItem.ToArray().AsSpan());
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="memoryTrace"></param>
    /// <remarks>Depends on <see cref="IsTracingMemory"/></remarks>
    void SetOperationMemory(TraceMemory memoryTrace);

    /// <summary>
    ///
    /// </summary>
    /// <param name="newSize"></param>
    /// <remarks>Depends on <see cref="IsTracingMemory"/></remarks>
    void SetOperationMemorySize(ulong newSize);

    /// <summary>
    ///
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="data"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data);

    /// <summary>
    ///
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="data"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportMemoryChange(UInt256 offset, in ReadOnlySpan<byte> data)
    {
        if (offset is { u1: <= 0, u2: <= 0, u3: <= 0, u0: <= long.MaxValue })
        {
            ReportMemoryChange((long)offset, data);
        }
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="data"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportMemoryChange(UInt256 offset, byte data)
    {
        ReportMemoryChange(offset, new[] { data });
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="data"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportMemoryChange(UInt256 offset, in ZeroPaddedSpan data)
    {
        ReportMemoryChange(offset, data.ToArray());
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="address"></param>
    /// <param name="storageIndex"></param>
    /// <param name="newValue"></param>
    /// <param name="currentValue"></param>
    /// <remarks>Depends on <see cref="IsTracingOpLevelStorage"/></remarks>
    void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue);

    /// <summary>
    ///
    /// </summary>
    /// <param name="storageCellAddress"></param>
    /// <param name="storageIndex"></param>
    /// <param name="newValue"></param>
    /// <param name="currentValue"></param>
    /// <remarks>Depends on <see cref="IsTracingOpLevelStorage"/></remarks>
    void SetOperationTransientStorage(Address storageCellAddress, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) { }

    /// <summary>
    ///
    /// </summary>
    /// <param name="address"></param>
    /// <param name="storageIndex"></param>
    /// <param name="value"></param>
    /// <remarks>Depends on <see cref="IsTracingOpLevelStorage"/></remarks>
    void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value);

    /// <summary>
    ///
    /// </summary>
    /// <param name="storageCellAddress"></param>
    /// <param name="storageIndex"></param>
    /// <param name="value"></param>
    /// <remarks>Depends on <see cref="IsTracingOpLevelStorage"/></remarks>
    void LoadOperationTransientStorage(Address storageCellAddress, UInt256 storageIndex, ReadOnlySpan<byte> value) { }

    /// <summary>
    ///
    /// </summary>
    /// <param name="address"></param>
    /// <param name="balance"></param>
    /// <param name="refundAddress"></param>
    /// <remarks>Depends on <see cref="IsTracingActions"/></remarks>
    void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress);

    /// <summary>
    ///
    /// </summary>
    /// <param name="gas"></param>
    /// <param name="value"></param>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <param name="input"></param>
    /// <param name="callType"></param>
    /// <param name="isPrecompileCall"></param>
    /// <remarks>Depends on <see cref="IsTracingActions"/></remarks>
    void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false);

    /// <summary>
    ///
    /// </summary>
    /// <param name="gas"></param>
    /// <param name="output"></param>
    /// <remarks>Depends on <see cref="IsTracingActions"/></remarks>
    void ReportActionEnd(long gas, ReadOnlyMemory<byte> output);

    /// <summary>
    ///
    /// </summary>
    /// <param name="gasLeft"></param>
    /// <param name="output"></param>
    /// <remarks>Depends on <see cref="IsTracingActions"/></remarks>
    void ReportActionRevert(long gasLeft, ReadOnlyMemory<byte> output);

    /// <summary>
    ///
    /// </summary>
    /// <param name="evmExceptionType"></param>
    /// <remarks>Depends on <see cref="IsTracingActions"/></remarks>
    void ReportActionError(EvmExceptionType evmExceptionType);

    /// <summary>
    ///
    /// </summary>
    /// <param name="gas"></param>
    /// <param name="deploymentAddress"></param>
    /// <param name="deployedCode"></param>
    /// <remarks>Depends on <see cref="IsTracingActions"/></remarks>
    void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode);

    /// <summary>
    ///
    /// </summary>
    /// <param name="blockHash"></param>
    /// <remarks>Depends on <see cref="IsTracingBlockHash"/></remarks>
    void ReportBlockHash(Hash256 blockHash);

    /// <summary>
    ///
    /// </summary>
    /// <param name="byteCode"></param>
    /// <remarks>Depends on <see cref="IsTracingCode"/></remarks>
    void ReportByteCode(ReadOnlyMemory<byte> byteCode);

    /// <summary>
    /// Special case for VM trace in Parity but we consider removing support for it
    /// </summary>
    /// <param name="refund"></param>
    /// <param name="gasAvailable"></param>
    /// <remarks>Depends on <see cref="IsTracingInstructions"/></remarks>
    void ReportGasUpdateForVmTrace(long refund, long gasAvailable);

    /// <summary>
    ///
    /// </summary>
    /// <param name="refund"></param>
    /// <remarks>Depends on <see cref="IsTracingRefunds"/></remarks>
    void ReportRefund(long refund);

    /// <summary>
    ///
    /// </summary>
    /// <param name="extraGasPressure"></param>
    /// <remarks>Depends on <see cref="IsTracingRefunds"/></remarks>
    void ReportExtraGasPressure(long extraGasPressure);

    /// <summary>
    /// Reports access to storage cell
    /// </summary>
    /// <param name="accessedAddresses">address</param>
    /// <param name="accessedStorageCells">cell</param>
    /// <remarks>Depends on <see cref="IsTracingAccess"/></remarks>
    void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells);

    /// <summary>
    /// Reports fees of a transaction
    /// </summary>
    /// <param name="fees">Fees sent to block author</param>
    /// <param name="burntFees">EIP-1559 burnt fees</param>
    /// <remarks>Depends on <see cref="IsTracingFees"/></remarks>
    void ReportFees(UInt256 fees, UInt256 burntFees);
}
