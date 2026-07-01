// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Abi;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Collections;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using System;
using System.Buffers.Binary;
using Nethermind.Core.Messages;

namespace Nethermind.Consensus.ExecutionRequests;

public class ExecutionRequestsProcessor : IExecutionRequestsProcessor
{
    public static readonly AbiSignature DepositEventAbi = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);

    private const long GasLimit = Eip8037Constants.SystemCallGasLimit;
    private const int AbiWordSize = 32;
    private const int DepositEventDataLength = 576;
    private const int DepositEventPubkeyOffset = 160;
    private const int DepositEventWithdrawalCredentialsOffset = 256;
    private const int DepositEventAmountOffset = 320;
    private const int DepositEventSignatureOffset = 384;
    private const int DepositEventIndexOffset = 512;

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
                ReadRequests(block, state, spec.Eip7002ContractAddress, ref requests, _withdrawalTransaction,
                    ExecutionRequestType.WithdrawalRequest,
                    BlockErrorMessages.WithdrawalsContractEmpty, BlockErrorMessages.WithdrawalsContractFailed);
            }

            if (spec.ConsolidationRequestsEnabled)
            {
                ReadRequests(block, state, spec.Eip7251ContractAddress, ref requests, _consolidationTransaction,
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

        Span<byte> depositRequestBuffer = stackalloc byte[ExecutionRequestExtensions.DepositRequestsBytesSize];
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
                        DecodeDepositRequest(block, log, depositRequestBuffer);
                        depositRequests.AddRange(depositRequestBuffer);
                    }
                }
            }
        }

        if (depositRequests.Count > 1)
            requests.Add(depositRequests.ToArray());
    }

    private static void DecodeDepositRequest(Block block, LogEntry log, Span<byte> buffer)
    {
        ValidateDepositEventLayout(block, log.Data);
        int offset = 0;

        CopyDepositEventField(log.Data, DepositEventPubkeyOffset, ExecutionRequestExtensions.PublicKeySize, buffer, ref offset);
        CopyDepositEventField(log.Data, DepositEventWithdrawalCredentialsOffset, ExecutionRequestExtensions.WithdrawalCredentialsSize, buffer, ref offset);
        CopyDepositEventField(log.Data, DepositEventAmountOffset, ExecutionRequestExtensions.AmountSize, buffer, ref offset);
        CopyDepositEventField(log.Data, DepositEventSignatureOffset, ExecutionRequestExtensions.SignatureSize, buffer, ref offset);
        CopyDepositEventField(log.Data, DepositEventIndexOffset, ExecutionRequestExtensions.IndexSize, buffer, ref offset);
    }

    private static void ValidateDepositEventLayout(Block block, byte[] data)
    {
        if (data.Length != DepositEventDataLength)
        {
            throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout($"Deposit event data length does not match, expected {DepositEventDataLength}, got {data.Length}."));
        }

        ValidateDepositEventWord(block, data, 0 * AbiWordSize, DepositEventPubkeyOffset, "pubkey offset");
        ValidateDepositEventWord(block, data, 1 * AbiWordSize, DepositEventWithdrawalCredentialsOffset, "withdrawal credentials offset");
        ValidateDepositEventWord(block, data, 2 * AbiWordSize, DepositEventAmountOffset, "amount offset");
        ValidateDepositEventWord(block, data, 3 * AbiWordSize, DepositEventSignatureOffset, "signature offset");
        ValidateDepositEventWord(block, data, 4 * AbiWordSize, DepositEventIndexOffset, "index offset");

        ValidateDepositEventWord(block, data, DepositEventPubkeyOffset, ExecutionRequestExtensions.PublicKeySize, "pubkey size");
        ValidateDepositEventWord(block, data, DepositEventWithdrawalCredentialsOffset, ExecutionRequestExtensions.WithdrawalCredentialsSize, "withdrawal credentials size");
        ValidateDepositEventWord(block, data, DepositEventAmountOffset, ExecutionRequestExtensions.AmountSize, "amount size");
        ValidateDepositEventWord(block, data, DepositEventSignatureOffset, ExecutionRequestExtensions.SignatureSize, "signature size");
        ValidateDepositEventWord(block, data, DepositEventIndexOffset, ExecutionRequestExtensions.IndexSize, "index size");
    }

    private static void ValidateDepositEventWord(Block block, byte[] data, int dataOffset, int expectedValue, string name)
    {
        ReadOnlySpan<byte> word = data.AsSpan(dataOffset, AbiWordSize);
        if (!word.Slice(0, AbiWordSize - sizeof(uint)).IsZero())
        {
            throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout($"Deposit event {name} does not match, expected {expectedValue}."));
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(word.Slice(AbiWordSize - sizeof(uint), sizeof(uint))) != (uint)expectedValue)
        {
            throw new InvalidBlockException(block, BlockErrorMessages.InvalidDepositEventLayout($"Deposit event {name} does not match, expected {expectedValue}."));
        }
    }

    private static void CopyDepositEventField(byte[] data, int dataOffset, int length, Span<byte> buffer, ref int bufferOffset)
    {
        data.AsSpan(dataOffset + AbiWordSize, length).CopyTo(buffer.Slice(bufferOffset, length));
        bufferOffset += length;
    }

    private void ReadRequests(Block block, IWorldState state, Address contractAddress, ref ArrayPoolListRef<byte[]> requests,
        Transaction systemTx, ExecutionRequestType type, string contractEmptyError, string contractFailedError)
    {
        if (!state.HasCode(contractAddress))
        {
            throw new InvalidBlockException(block, contractEmptyError);
        }

        CallOutputTracer tracer = new();

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
