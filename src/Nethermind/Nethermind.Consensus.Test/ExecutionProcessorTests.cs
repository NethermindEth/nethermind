// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Tracing;

namespace Nethermind.Consensus.Test;

public class ExecutionProcessorTests
{
    private ISpecProvider _specProvider;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IReleaseSpec _spec;
    private IDisposable _worldStateCloser;
    private static readonly UInt256 AccountBalance = 1.Ether;
    private static readonly Address DepositContractAddress = Eip6110Constants.MainnetDepositContractAddress;
    private static readonly Address eip7002Account = Eip7002Constants.WithdrawalRequestPredeployAddress;
    private static readonly Address eip7251Account = Eip7251Constants.ConsolidationRequestPredeployAddress;
    private static readonly AbiSignature _depositEventABI = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    private static readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;
    private const int AbiWordSize = 32;
    private const int DepositEventDataLength = 576;
    private const int DepositEventPubkeyOffset = 160;
    private const int DepositEventWithdrawalCredentialsOffset = 256;
    private const int DepositEventAmountOffset = 320;
    private const int DepositEventSignatureOffset = 384;
    private const int DepositEventIndexOffset = 512;

    private static readonly TestExecutionRequest[] _executionDepositRequests = [TestItem.ExecutionRequestA, TestItem.ExecutionRequestB, TestItem.ExecutionRequestC];
    private static readonly TestExecutionRequest[] _executionWithdrawalRequests = [TestItem.ExecutionRequestD, TestItem.ExecutionRequestE, TestItem.ExecutionRequestF];
    private static readonly TestExecutionRequest[] _executionConsolidationRequests = [TestItem.ExecutionRequestG, TestItem.ExecutionRequestH, TestItem.ExecutionRequestI];

    private static void FlatEncodeWithoutType(ExecutionRequest[] requests, Span<byte> buffer)
    {
        int currentPosition = 0;

        foreach (ExecutionRequest request in requests)
        {
            if (currentPosition + request.RequestData!.Length > buffer.Length)
            {
                throw new InvalidOperationException("Buffer is not large enough to hold all data of requests");
            }

            request.RequestData.CopyTo(buffer.Slice(currentPosition, request.RequestData.Length));
            currentPosition += request.RequestData.Length;
        }
    }

    [SetUp]
    public void Setup()
    {
        _specProvider = MainnetSpecProvider.Instance;
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(eip7002Account, AccountBalance);
        _stateProvider.CreateAccount(eip7251Account, AccountBalance);

        _stateProvider.InsertCode(eip7002Account, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, Prague.Instance);
        _stateProvider.InsertCode(eip7251Account, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, Prague.Instance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        _spec = ReleaseSpecSubstitute.Create();

        _spec.DepositsEnabled.Returns(true);
        _spec.WithdrawalRequestsEnabled.Returns(true);
        _spec.ConsolidationRequestsEnabled.Returns(true);
        _spec.BuilderRequestsEnabled.Returns(false);

        _spec.DepositContractAddress.Returns(DepositContractAddress);
        _spec.Eip7002ContractAddress.Returns(eip7002Account);
        _spec.Eip7251ContractAddress.Returns(eip7251Account);

        _transactionProcessor = Substitute.For<ITransactionProcessor>();

        _transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<CallOutputTracer>())
            .Returns(ci =>
            {
                Transaction transaction = ci.Arg<Transaction>();
                CallOutputTracer tracer = ci.Arg<CallOutputTracer>();

                tracer.StatusCode = StatusCode.Success;

                if (transaction.To == eip7002Account)
                {
                    Span<byte> buffer = new byte[GetRequestsByteSize(_executionWithdrawalRequests)];
                    FlatEncodeWithoutType(_executionWithdrawalRequests, buffer);
                    tracer.ReturnValue = buffer.ToArray();
                }
                else if (transaction.To == eip7251Account)
                {
                    Span<byte> buffer = new byte[GetRequestsByteSize(_executionConsolidationRequests)];
                    FlatEncodeWithoutType(_executionConsolidationRequests, buffer);
                    tracer.ReturnValue = buffer.ToArray();
                }
                else
                {
                    tracer.ReturnValue = [];
                }
                return new TransactionResult();

                static int GetRequestsByteSize(IEnumerable<ExecutionRequest> requests) => requests.Sum(r => r.RequestData.Length);
            });
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    private static Hash256 CalculateHash(
        TestExecutionRequest[] depositRequests,
        TestExecutionRequest[] withdrawalRequests,
        TestExecutionRequest[] consolidationRequests
    )
    {
        using ArrayPoolList<byte[]> requests = TestExecutionRequestExtensions.GetFlatEncodedRequests(depositRequests, withdrawalRequests, consolidationRequests);
        return ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(requests.ToArray());
    }

    private Hash256 ProcessBlockAndGetRequestsHash(ulong blockNumber, TxReceipt[] txReceipts)
    {
        Block block = Build.A.Block.WithNumber(blockNumber).TestObject;
        ExecutionRequestsProcessor executionRequestsProcessor = new(_transactionProcessor);
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _spec));
        executionRequestsProcessor.ProcessExecutionRequests(block, _stateProvider, txReceipts, _spec);
        return block.Header.RequestsHash;
    }

    [Test]
    public void ShouldNotProcessExecutionRequestsForGenesisBlock()
    {
        TxReceipt[] txReceipts = [
            Build.A.Receipt.WithLogs(
                CreateLogEntry(TestItem.ExecutionRequestA.RequestDataParts)
            ).TestObject
        ];

        Hash256 requestsHash = ProcessBlockAndGetRequestsHash(0, txReceipts);

        Assert.That(requestsHash, Is.Null);
    }

    [Test]
    public void ShouldProcessExecutionRequests()
    {
        TxReceipt[] txReceipts = [
            Build.A.Receipt.WithLogs(
                CreateLogEntry(TestItem.ExecutionRequestA.RequestDataParts),
                CreateLogEntry(TestItem.ExecutionRequestB.RequestDataParts),
                CreateLogEntry(TestItem.ExecutionRequestC.RequestDataParts)
            ).TestObject
        ];

        Hash256 requestsHash = ProcessBlockAndGetRequestsHash(1, txReceipts);

        Assert.That(requestsHash, Is.EqualTo(
           CalculateHash(_executionDepositRequests, _executionWithdrawalRequests, _executionConsolidationRequests)
       ));
    }

    private static LogEntry CreateLogEntry(byte[][] requestDataParts) =>
        Build.A.LogEntry
            .WithData(_abiEncoder.Encode(AbiEncodingStyle.None, _depositEventABI, requestDataParts!))
            .WithTopics(ExecutionRequestsProcessor.DepositEventAbi.Hash)
            .WithAddress(DepositContractAddress).TestObject;

    [Test]
    public void ShouldRejectDepositLogWithNonCanonicalAbiOffsets()
    {
        TxReceipt[] txReceipts = [
            Build.A.Receipt.WithLogs(
                CreateLogEntryWithSwappedAmountAndSignatureSlots(TestItem.ExecutionRequestA.RequestDataParts)
            ).TestObject
        ];

        // EIP-6110 mandates the exact canonical ABI layout, so any deviation in the offset words
        // invalidates the block even when every field still decodes to a correctly-sized byte array.
        Assert.Throws<InvalidBlockException>(() => ProcessBlockAndGetRequestsHash(1, txReceipts));
    }

    [TestCase(DepositEventLayoutMutation.ExtraTrailingWord)]
    [TestCase(DepositEventLayoutMutation.WrongAmountSize)]
    [TestCase(DepositEventLayoutMutation.NonZeroHighByteInOffset)]
    public void ShouldRejectDepositLogWithInvalidCanonicalAbiLayout(DepositEventLayoutMutation mutation)
    {
        byte[] data = BuildCanonicalDepositEventData(TestItem.ExecutionRequestA.RequestDataParts);
        ApplyDepositEventLayoutMutation(data, mutation, out byte[] mutatedData);
        TxReceipt[] txReceipts = [
            Build.A.Receipt.WithLogs(
                CreateDepositLogEntry(mutatedData)
            ).TestObject
        ];

        Assert.Throws<InvalidBlockException>(() => ProcessBlockAndGetRequestsHash(1, txReceipts));
    }

    private static LogEntry CreateLogEntryWithSwappedAmountAndSignatureSlots(byte[][] requestDataParts) =>
        CreateDepositLogEntry(BuildDepositEventDataWithSwappedAmountAndSignatureSlots(requestDataParts!));

    private static LogEntry CreateDepositLogEntry(byte[] data) =>
        Build.A.LogEntry
            .WithData(data)
            .WithTopics(ExecutionRequestsProcessor.DepositEventAbi.Hash)
            .WithAddress(DepositContractAddress).TestObject;

    private static byte[] BuildCanonicalDepositEventData(byte[][] requestDataParts)
    {
        byte[] data = new byte[DepositEventDataLength];

        WriteDepositEventOffset(data, 0, DepositEventPubkeyOffset);
        WriteDepositEventOffset(data, 1, DepositEventWithdrawalCredentialsOffset);
        WriteDepositEventOffset(data, 2, DepositEventAmountOffset);
        WriteDepositEventOffset(data, 3, DepositEventSignatureOffset);
        WriteDepositEventOffset(data, 4, DepositEventIndexOffset);

        WriteDepositEventField(data, DepositEventPubkeyOffset, requestDataParts[0]);
        WriteDepositEventField(data, DepositEventWithdrawalCredentialsOffset, requestDataParts[1]);
        WriteDepositEventField(data, DepositEventAmountOffset, requestDataParts[2]);
        WriteDepositEventField(data, DepositEventSignatureOffset, requestDataParts[3]);
        WriteDepositEventField(data, DepositEventIndexOffset, requestDataParts[4]);

        return data;
    }

    private static byte[] BuildDepositEventDataWithSwappedAmountAndSignatureSlots(byte[][] requestDataParts)
    {
        byte[] data = new byte[DepositEventDataLength];

        WriteDepositEventOffset(data, 0, DepositEventPubkeyOffset);
        WriteDepositEventOffset(data, 1, DepositEventWithdrawalCredentialsOffset);
        WriteDepositEventOffset(data, 2, 448); // amount offset: canonical is 320
        WriteDepositEventOffset(data, 3, 320); // signature offset: canonical is 384
        WriteDepositEventOffset(data, 4, DepositEventIndexOffset);

        WriteDepositEventField(data, DepositEventPubkeyOffset, requestDataParts[0]);
        WriteDepositEventField(data, DepositEventWithdrawalCredentialsOffset, requestDataParts[1]);
        WriteDepositEventField(data, DepositEventAmountOffset, requestDataParts[3]);
        WriteDepositEventField(data, 448, requestDataParts[2]);
        WriteDepositEventField(data, DepositEventIndexOffset, requestDataParts[4]);

        return data;
    }

    private static void WriteDepositEventOffset(byte[] data, int wordIndex, int offset) =>
        WriteDepositEventWord(data, wordIndex * AbiWordSize, offset);

    private static void WriteDepositEventField(byte[] data, int offset, byte[] field)
    {
        WriteDepositEventWord(data, offset, field.Length);
        field.CopyTo(data.AsSpan(offset + AbiWordSize));
    }

    private static void WriteDepositEventWord(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + AbiWordSize - sizeof(int), sizeof(int)), value);

    private static void ApplyDepositEventLayoutMutation(byte[] data, DepositEventLayoutMutation mutation, out byte[] mutatedData)
    {
        mutatedData = data;
        switch (mutation)
        {
            case DepositEventLayoutMutation.ExtraTrailingWord:
                Array.Resize(ref mutatedData, DepositEventDataLength + AbiWordSize);
                break;
            case DepositEventLayoutMutation.WrongAmountSize:
                WriteDepositEventWord(mutatedData, DepositEventAmountOffset, ExecutionRequestExtensions.AmountSize + 1);
                break;
            case DepositEventLayoutMutation.NonZeroHighByteInOffset:
                mutatedData[0] = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    [Test]
    public void ShouldUseCorrectDepositTopic() => Assert.That(ExecutionRequestsProcessor.DepositEventAbi.Hash, Is.EqualTo(new Hash256("0x649bbc62d0e31342afea4e5cd82d4049e7e1ee912fc0889aa790803be39038c5")));

    public enum DepositEventLayoutMutation
    {
        ExtraTrailingWord,
        WrongAmountSize,
        NonZeroHighByteInOffset
    }
}
