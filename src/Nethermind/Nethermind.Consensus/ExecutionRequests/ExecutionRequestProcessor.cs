// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

public class ExecutionRequestsProcessor(ITransactionProcessor transactionProcessor) : IExecutionRequestProcessor
{
    private readonly AbiSignature _depositEventABI = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    private readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;
    private const int depositRequestsBytesSize = 48 + 32 + 8 + 96 + 8;
    private const int withdrawalRequestsBytesSize = 20 + 48 + 8;
    private const int consolidationRequestsBytesSize = 20 + 48 + 48;

    private const long GasLimit = 30_000_000L;

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
                    if (log.LoggersAddress == spec.DepositContractAddress)
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
        List<byte> flattenedResult = new List<byte>();

        foreach (var item in result)
        {
            if (item is byte[] byteArray)
            {
                flattenedResult.AddRange(byteArray);
            }
            else
            {
                throw new InvalidOperationException("Decoded ABI result contains non-byte array elements.");
            }
        }

        // make sure the flattened result is of the correct size
        if (flattenedResult.Count != depositRequestsBytesSize)
        {
            throw new InvalidOperationException($"Decoded ABI result has incorrect size. Expected {depositRequestsBytesSize} bytes, got {flattenedResult.Count} bytes.");
        }

        return new ExecutionRequest
        {
            RequestType = (byte)ExecutionRequestType.Deposit,
            RequestData = flattenedResult.ToArray()
        };
    }


    private IEnumerable<ExecutionRequest> ReadRequests(Block block, IWorldState state, IReleaseSpec spec, Address contractAddress)
    {
        bool IsWithdrawalRequests = contractAddress == spec.Eip7002ContractAddress;

        int requestBytesSize = IsWithdrawalRequests ? withdrawalRequestsBytesSize : consolidationRequestsBytesSize;

        if (!(IsWithdrawalRequests ? spec.WithdrawalRequestsEnabled : spec.ConsolidationRequestsEnabled))
            yield break;

        if (!state.AccountExists(contractAddress))
            yield break;

        CallOutputTracer tracer = new();

        Transaction? transaction = new()
        {
            Value = UInt256.Zero,
            Data = Array.Empty<byte>(),
            To = contractAddress,
            SenderAddress = Address.SystemUser,
            GasLimit = GasLimit,
            GasPrice = UInt256.Zero,
        };
        transaction.Hash = transaction.CalculateHash();

        transactionProcessor.Execute(transaction, new BlockExecutionContext(block.Header), tracer);
        var result = tracer.ReturnValue;
        if (result == null || result.Length == 0)
            yield break;

        int requestCount = result.Length / requestBytesSize;

        for (int i = 0; i < requestCount; i++)
        {
            int offset = i * requestBytesSize;
            yield return new ExecutionRequest
            {
                RequestType = (byte)(IsWithdrawalRequests ? ExecutionRequestType.WithdrawalRequest : ExecutionRequestType.ConsolidationRequest),
                RequestData = result.Slice(offset, requestBytesSize).ToArray()
            };
        }

    }

    public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.RequestsEnabled)
            return;

        List<ExecutionRequest> requests =
        [
            .. ProcessDeposits(receipts, spec),
            .. ReadRequests(block, state, spec, spec.Eip7002ContractAddress),
            .. ReadRequests(block, state, spec, spec.Eip7251ContractAddress),
        ];
        block.Header.RequestsHash = requests.ToArray().CalculateRoot();
        block.ExecutionRequests = requests.ToArray();
    }
}