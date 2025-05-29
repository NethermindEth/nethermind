// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Consensus.Messages;
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
using System;
using System.Linq;

namespace Nethermind.Consensus.ExecutionRequests;

public class ExecutionRequestsProcessor : IExecutionRequestsProcessor
{
    public static readonly AbiSignature DepositEventAbi = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    private readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;

    private const long GasLimit = 30_000_000L;

    private readonly ITransactionProcessor _transactionProcessor;

    private readonly SystemCall _withdrawalTransaction = new()
    {
        Value = UInt256.Zero,
        Data = Array.Empty<byte>(),
        To = Eip7002Constants.WithdrawalRequestPredeployAddress,
        SenderAddress = Address.SystemUser,
        GasLimit = GasLimit,
        GasPrice = UInt256.Zero,
    };

    private readonly SystemCall _consolidationTransaction = new()
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

    public void ProcessExecutionRequests(Block block, IWorldState state, in BlockExecutionContext blkCtx, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.RequestsEnabled || block.IsGenesis)
            return;

        using ArrayPoolList<byte[]> requests = new(3);

        ProcessDeposits(block, receipts, spec, requests);

        if (spec.WithdrawalRequestsEnabled)
        {
            ReadRequests(block, state, in blkCtx, spec.Eip7002ContractAddress, requests, _withdrawalTransaction, ExecutionRequestType.WithdrawalRequest,
                BlockErrorMessages.WithdrawalsContractEmpty, BlockErrorMessages.WithdrawalsContractFailed);
        }

        if (spec.ConsolidationRequestsEnabled)
        {
            ReadRequests(block, state, in blkCtx, spec.Eip7251ContractAddress, requests, _consolidationTransaction, ExecutionRequestType.ConsolidationRequest,
                BlockErrorMessages.ConsolidationsContractEmpty, BlockErrorMessages.ConsolidationsContractFailed);
        }

        block.ExecutionRequests = [.. requests];
        block.Header.RequestsHash = ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(block.ExecutionRequests);
    }

    private void ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec, ArrayPoolList<byte[]> requests)
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
                    if (log.Address == spec.DepositContractAddress && log.Topics.Length >= 1 && log.Topics[0] == DepositEventAbi.Hash)
                    {
                        Span<byte> depositRequestBuffer = new byte[ExecutionRequestExtensions.DepositRequestsBytesSize];
                        DecodeDepositRequest(block, log, depositRequestBuffer);
                        depositRequests.AddRange(depositRequestBuffer.ToArray());
                    }
                }
            }
        }

        if (depositRequests.Count > 1)
            requests.Add(depositRequests.ToArray());
    }

    private void DecodeDepositRequest(Block block, LogEntry log, Span<byte> buffer)
    {
        object[] result = null;
        try
        {
            result = _abiEncoder.Decode(AbiEncodingStyle.None, DepositEventAbi, log.Data);
        }
        catch (Exception e) when (e is AbiException or OverflowException)
        {
            throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout(e.Message));
        }

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
                throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout("Decoded ABI result contains non-byte array elements."));
            }
        }

        // make sure the flattened result is of the correct size
        if (offset != ExecutionRequestExtensions.DepositRequestsBytesSize)
        {
            throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout($"Decoded ABI result has incorrect size. Expected {ExecutionRequestExtensions.DepositRequestsBytesSize} bytes, got {offset} bytes."));
        }
    }

    private void ReadRequests(Block block, IWorldState state, in BlockExecutionContext blkCtx, Address contractAddress, ArrayPoolList<byte[]> requests,
        Transaction systemTx, ExecutionRequestType type, string contractEmptyError, string contractFailedError)
    {
        if (!state.HasCode(contractAddress))
        {
            throw new InvalidBlockException(block, contractEmptyError);
        }

        CallOutputTracer tracer = new();

        _transactionProcessor.Execute(systemTx, in blkCtx, tracer);

        if (tracer.StatusCode == StatusCode.Failure)
        {
            throw new InvalidBlockException(block, contractFailedError);
        }

        if (tracer.ReturnValue is null || tracer.ReturnValue.Length == 0)
        {
            return;
        }

        byte[] buffer = new byte[tracer.ReturnValue.Length + 1];
        buffer[0] = (byte)type;
        tracer.ReturnValue.AsSpan().CopyTo(buffer.AsSpan(1));
        requests.Add(buffer);
    }
}
