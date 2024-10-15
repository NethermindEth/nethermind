// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Collections;
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

public class ExecutionRequestsProcessor(ITransactionProcessor transactionProcessor) : IExecutionRequestsProcessor
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
        byte[] flattenedResult = new byte[depositRequestsBytesSize];
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
        if (offset != depositRequestsBytesSize)
        {
            throw new InvalidOperationException($"Decoded ABI result has incorrect size. Expected {depositRequestsBytesSize} bytes, got {offset} bytes.");
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

        int requestBytesSize = isWithdrawalRequests ? withdrawalRequestsBytesSize : consolidationRequestsBytesSize;

        if (!(isWithdrawalRequests ? spec.WithdrawalRequestsEnabled : spec.ConsolidationRequestsEnabled))
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
                RequestType = (byte)(isWithdrawalRequests ? ExecutionRequestType.WithdrawalRequest : ExecutionRequestType.ConsolidationRequest),
                RequestData = result.Slice(offset, requestBytesSize).ToArray()
            };
        }

    }

    public Hash256 CalculateRequestsHash(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec, out ArrayPoolList<ExecutionRequest> requests)
    {
        ArrayPoolList<ExecutionRequest> requestsList = new ArrayPoolList<ExecutionRequest>(receipts.Length * 2);
        using (SHA256 sha256 = SHA256.Create())
        {
            using (SHA256 sha256Inner = SHA256.Create())
            {
                void ProcessAndHashRequests(IEnumerable<ExecutionRequest> executionRequests)
                {
                    foreach (ExecutionRequest request in executionRequests)
                    {
                        requestsList.AddRange(request);
                        var internalBuffer = new byte[request.RequestData.Length + 1];
                        request.FlatEncode(internalBuffer);
                        byte[] requestHash = sha256Inner.ComputeHash(internalBuffer);

                        // Update the outer hash with the result of each inner hash
                        sha256.TransformBlock(requestHash, 0, requestHash.Length, null, 0);
                    }
                }

                ProcessAndHashRequests(ProcessDeposits(receipts, spec));
                ProcessAndHashRequests(ReadRequests(block, state, spec, spec.Eip7002ContractAddress));
                ProcessAndHashRequests(ReadRequests(block, state, spec, spec.Eip7251ContractAddress));

                // Complete the final hash computation
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                requests = requestsList;
                return new Hash256(sha256.Hash!);
            }
        }
    }

    public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.RequestsEnabled)
            return;
        block.Header.RequestsHash = CalculateRequestsHash(block, state, receipts, spec, out ArrayPoolList<ExecutionRequest> requests);
        block.ExecutionRequests = requests;
    }
}
