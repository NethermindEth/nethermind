// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.ExecutionRequests;

public class ExecutionRequestsProcessor : IExecutionRequestsProcessor
{
    private readonly AbiSignature _depositEventABI = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    private readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;

    private const long GasLimit = 30_000_000L;

    private readonly ITransactionProcessor _transactionProcessor;

    private readonly Transaction _withdrawalTransaction = new()
    {
        Value = UInt256.Zero,
        Data = Array.Empty<byte>(),
        To = Eip7002Constants.WithdrawalRequestPredeployAddress,
        SenderAddress = Address.SystemUser,
        GasLimit = GasLimit,
        GasPrice = UInt256.Zero,
    };

    private readonly Transaction _consolidationTransaction = new()
    {
        Value = UInt256.Zero,
        Data = Array.Empty<byte>(),
        To = Eip7251Constants.ConsolidationRequestPredeployAddress,
        SenderAddress = Address.SystemUser,
        GasLimit = GasLimit,
        GasPrice = UInt256.Zero,
    };

    public ExecutionRequestsProcessor(ITransactionProcessor transactionProcessor)
    {
        _transactionProcessor = transactionProcessor;
        _withdrawalTransaction.Hash = _withdrawalTransaction.CalculateHash();
        _consolidationTransaction.Hash = _consolidationTransaction.CalculateHash();
    }

    public void ProcessDeposits(TxReceipt[] receipts, IReleaseSpec spec, ArrayPoolList<byte[]> requests)
    {
        if (!spec.DepositsEnabled)
            return;

        using ArrayPoolList<byte> depositRequests = new(receipts.Length * 2 + 1)
        {
            (byte)ExecutionRequestType.Deposit
        };

        for (int i = 0; i < receipts.Length; i++)
        {
            LogEntry[]? logEntries = receipts[i].Logs;
            if (logEntries is not null)
            {
                for (var j = 0; j < logEntries.Length; j++)
                {
                    LogEntry log = logEntries[j];
                    if (log.Address == spec.DepositContractAddress)
                    {
                        Span<byte> depositRequestBuffer = new byte[ExecutionRequestExtensions.DepositRequestsBytesSize];
                        DecodeDepositRequest(log, depositRequestBuffer);
                        depositRequests.AddRange(depositRequestBuffer.ToArray());
                    }
                }
            }
        }

        if (depositRequests.Count > 1)
            requests.Add(depositRequests.ToArray());
    }

    private void DecodeDepositRequest(LogEntry log, Span<byte> buffer)
    {
        object[] result = _abiEncoder.Decode(AbiEncodingStyle.None, _depositEventABI, log.Data);
        int offset = 0;

        foreach (var item in result)
        {
            if (item is byte[] byteArray)
            {
                byteArray.CopyTo(buffer.Slice(offset, byteArray.Length));
                offset += byteArray.Length;
            }
            else
            {
                throw new InvalidOperationException("Decoded ABI result contains non-byte array elements.");
            }
        }

        // make sure the flattened result is of the correct size
        if (offset != ExecutionRequestExtensions.DepositRequestsBytesSize)
        {
            throw new InvalidOperationException($"Decoded ABI result has incorrect size. Expected {ExecutionRequestExtensions.DepositRequestsBytesSize} bytes, got {offset} bytes.");
        }
    }

    private void ReadRequests(Block block, IWorldState state, IReleaseSpec spec, Address contractAddress, ArrayPoolList<byte[]> requests)
    {
        bool isWithdrawalRequests = contractAddress == spec.Eip7002ContractAddress;

        int requestsByteSize = isWithdrawalRequests ? ExecutionRequestExtensions.WithdrawalRequestsBytesSize : ExecutionRequestExtensions.ConsolidationRequestsBytesSize;

        if (!(isWithdrawalRequests ? spec.WithdrawalRequestsEnabled : spec.ConsolidationRequestsEnabled) || !state.AccountExists(contractAddress))
            return;

        CallOutputTracer tracer = new();

        _transactionProcessor.Execute(isWithdrawalRequests ? _withdrawalTransaction : _consolidationTransaction, new BlockExecutionContext(block.Header), tracer);

        if (tracer.ReturnValue is null || tracer.ReturnValue.Length == 0)
        {
            return;
        }

        int validLength = tracer.ReturnValue.Length - (tracer.ReturnValue.Length % requestsByteSize);

        if (validLength == 0) return;

        Span<byte> buffer = stackalloc byte[validLength + 1];
        buffer[0] = isWithdrawalRequests ? (byte)ExecutionRequestType.WithdrawalRequest : (byte)ExecutionRequestType.ConsolidationRequest;
        tracer.ReturnValue.AsSpan(0, validLength).CopyTo(buffer.Slice(1));
        requests.Add(buffer.ToArray());
    }

    public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.RequestsEnabled)
            return;

        using ArrayPoolList<byte[]> requests = new(3);

        ProcessDeposits(receipts, spec, requests);
        ReadRequests(block, state, spec, spec.Eip7002ContractAddress, requests);
        ReadRequests(block, state, spec, spec.Eip7251ContractAddress, requests);
        block.ExecutionRequests = requests.ToArray();
        block.Header.RequestsHash = ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(block.ExecutionRequests);
    }
}
