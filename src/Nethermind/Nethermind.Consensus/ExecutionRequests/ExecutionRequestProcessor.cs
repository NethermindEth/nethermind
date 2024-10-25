// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
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

    public IEnumerable<ExecutionRequest> ProcessDeposits(TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.DepositsEnabled)
            yield break;

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
                        yield return DecodeDepositRequest(log);
                    }
                }
            }
        }
    }

    private ExecutionRequest DecodeDepositRequest(LogEntry log)
    {
        object[] result = _abiEncoder.Decode(AbiEncodingStyle.None, _depositEventABI, log.Data);
        byte[] flattenedResult = new byte[ExecutionRequestExtensions.depositRequestsBytesSize];
        int offset = 0;

        foreach (var item in result)
        {
            if (item is byte[] byteArray)
            {
                Array.Copy(byteArray, 0, flattenedResult, offset, byteArray.Length);
                offset += byteArray.Length;
            }
            else
            {
                throw new InvalidOperationException("Decoded ABI result contains non-byte array elements.");
            }
        }

        // make sure the flattened result is of the correct size
        if (offset != ExecutionRequestExtensions.depositRequestsBytesSize)
        {
            throw new InvalidOperationException($"Decoded ABI result has incorrect size. Expected {ExecutionRequestExtensions.depositRequestsBytesSize} bytes, got {offset} bytes.");
        }

        return new ExecutionRequest
        {
            RequestType = (byte)ExecutionRequestType.Deposit,
            RequestData = flattenedResult
        };
    }


    private IEnumerable<ExecutionRequest> ReadRequests(Block block, IWorldState state, IReleaseSpec spec, Address contractAddress)
    {
        bool isWithdrawalRequests = contractAddress == spec.Eip7002ContractAddress;

        int requestBytesSize = isWithdrawalRequests ? ExecutionRequestExtensions.withdrawalRequestsBytesSize : ExecutionRequestExtensions.consolidationRequestsBytesSize;

        if (!(isWithdrawalRequests ? spec.WithdrawalRequestsEnabled : spec.ConsolidationRequestsEnabled))
            yield break;

        if (!state.AccountExists(contractAddress))
            yield break;

        CallOutputTracer tracer = new();

        _transactionProcessor.Execute(isWithdrawalRequests ? _withdrawalTransaction : _consolidationTransaction, new BlockExecutionContext(block.Header), tracer);
        var result = tracer.ReturnValue;
        if (result == null || result.Length == 0)
            yield break;

        int requestCount = result.Length / requestBytesSize;

        for (int i = 0; i < requestCount; i++)
        {
            int offset = i * requestBytesSize;
            yield return new ExecutionRequest
            {
                RequestType = (byte)(isWithdrawalRequests ? ExecutionRequestType.WithdrawalRequest : ExecutionRequestType.ConsolidationRequest),
                RequestData = result.Slice(offset, requestBytesSize).ToArray()
            };
        }

    }

    public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.RequestsEnabled)
            return;
        IEnumerable<ExecutionRequest> depositRequests = ProcessDeposits(receipts, spec);
        IEnumerable<ExecutionRequest> withdrawalRequests = ReadRequests(block, state, spec, spec.Eip7002ContractAddress);
        IEnumerable<ExecutionRequest> consolidationRequests = ReadRequests(block, state, spec, spec.Eip7251ContractAddress);
        using ArrayPoolList<byte[]> requests = ExecutionRequestExtensions.GetFlatEncodedRequests(depositRequests, withdrawalRequests, consolidationRequests);
        block.ExecutionRequests = requests.ToArray();
        block.Header.RequestsHash = ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(block.ExecutionRequests);
    }
}
