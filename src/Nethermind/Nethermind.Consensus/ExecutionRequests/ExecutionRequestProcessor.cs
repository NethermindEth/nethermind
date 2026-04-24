// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Abi;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using System;
using Nethermind.Core.Messages;

namespace Nethermind.Consensus.ExecutionRequests;

public class ExecutionRequestsProcessor(ITransactionProcessor transactionProcessor) : IExecutionRequestsProcessor
{
    public static readonly AbiSignature DepositEventAbi = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    private readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;

    private readonly ITransactionProcessor _transactionProcessor = transactionProcessor;

    public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.RequestsEnabled || block.IsGenesis)
            return;

        ArrayPoolListRef<byte[]> requests = new(3);
        try
        {
            ProcessDeposits(block, receipts, spec, ref requests);

            if (spec.WithdrawalRequestsEnabled)
            {
                ReadRequests(block, state, spec.Eip7002ContractAddress, ref requests,
                    ExecutionRequestType.WithdrawalRequest,
                    BlockErrorMessages.WithdrawalsContractEmpty, BlockErrorMessages.WithdrawalsContractFailed);
            }

            if (spec.ConsolidationRequestsEnabled)
            {
                ReadRequests(block, state, spec.Eip7251ContractAddress, ref requests,
                    ExecutionRequestType.ConsolidationRequest,
                    BlockErrorMessages.ConsolidationsContractEmpty, BlockErrorMessages.ConsolidationsContractFailed);
            }

            block.ExecutionRequests = [.. requests];
            block.Header.RequestsHash =
                ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(block.ExecutionRequests);
        }
        finally
        {
            requests.Dispose();
        }
    }

    private void ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec, ref ArrayPoolListRef<byte[]> requests)
    {
        if (!spec.DepositsEnabled)
            return;

        using ArrayPoolListRef<byte> depositRequests = new(receipts.Length * 2 + 1);
        depositRequests.Add((byte)ExecutionRequestType.Deposit);

        for (int i = 0; i < receipts.Length; i++)
        {
            LogEntry[]? logEntries = receipts[i].Logs;
            if (logEntries is not null)
            {
                for (int j = 0; j < logEntries.Length; j++)
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
        object[] result;
        try
        {
            result = _abiEncoder.Decode(AbiEncodingStyle.None, DepositEventAbi, log.Data);
            ValidateLayout(result, block);
        }
        catch (AbiException e)
        {
            throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout(e.Message), e);
        }

        int offset = 0;

        foreach (object item in result)
        {
            if (item is byte[] byteArray)
            {
                byteArray.CopyTo(buffer.Slice(offset, byteArray.Length));
                offset += byteArray.Length;
            }
        }
    }

    private static void ValidateLayout(object[] result, Block block)
    {
        Validate(block, result[0], "pubkey", ExecutionRequestExtensions.PublicKeySize);
        Validate(block, result[1], "withdrawalCredentials", ExecutionRequestExtensions.WithdrawalCredentialsSize);
        Validate(block, result[2], "amount", ExecutionRequestExtensions.AmountSize);
        Validate(block, result[3], "signature", ExecutionRequestExtensions.SignatureSize);
        Validate(block, result[4], "index", ExecutionRequestExtensions.IndexSize);

        static void Validate(Block block, object obj, string name, int expectedSize)
        {
            if (obj is not byte[] byteArray)
            {
                throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout($"Decoded ABI result contains {name} as non-byte array element."));
            }

            if (byteArray.Length != expectedSize)
            {
                throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout($"Decoded ABI result contains invalid {name} element, size does not match, expected {expectedSize}, got {byteArray.Length}."));
            }
        }
    }

    private void ReadRequests(Block block, IWorldState state, Address contractAddress, ref ArrayPoolListRef<byte[]> requests,
        ExecutionRequestType type, string contractEmptyError, string contractFailedError)
    {
        if (!state.HasCode(contractAddress))
        {
            throw new InvalidBlockException(block, contractEmptyError);
        }

        CallOutputTracer tracer = new();
        SystemCall systemTx = SystemCallFactory.Create(contractAddress, block.Header.BaseFeePerGas);

        _transactionProcessor.Execute(systemTx, tracer);

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
