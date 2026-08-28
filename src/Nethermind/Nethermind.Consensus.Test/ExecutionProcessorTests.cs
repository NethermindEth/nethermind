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
using Nethermind.Core.Messages;
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
    private const int DepositEventFieldCount = 5;
    private const int PubkeyHeadWord = 0;
    private const int AmountHeadWord = 2;

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
        CreateDepositLogEntry(EncodeDepositEventData(requestDataParts));

    private static LogEntry CreateDepositLogEntry(byte[] data) =>
        Build.A.LogEntry
            .WithData(data)
            .WithTopics(ExecutionRequestsProcessor.DepositEventAbi.Hash)
            .WithAddress(DepositContractAddress).TestObject;

    private static byte[] EncodeDepositEventData(byte[][] requestDataParts) =>
        _abiEncoder.Encode(AbiEncodingStyle.None, _depositEventABI, requestDataParts);

    [TestCase(DepositEventLayoutMutation.SwappedPubkeyAndCredentialsBlocks)]
    [TestCase(DepositEventLayoutMutation.SwappedAmountAndSignatureBlocks)]
    [TestCase(DepositEventLayoutMutation.BlocksShiftedByOneWord)]
    [TestCase(DepositEventLayoutMutation.OffsetBeyondData)]
    [TestCase(DepositEventLayoutMutation.OffsetHighBitsSet)]
    [TestCase(DepositEventLayoutMutation.ExtraTrailingWord)]
    [TestCase(DepositEventLayoutMutation.TruncatedData)]
    public void ShouldRejectDepositLogWithNonCanonicalAbiLayout(DepositEventLayoutMutation mutation) =>
        AssertDepositLogRejected(ApplyDepositEventLayoutMutation(CanonicalDepositEventData(), mutation));

    /// <summary>
    /// Every word the canonical layout pins — the five head offsets followed by the five field length words
    /// they point at — has to match exactly, so incrementing any one of them must invalidate the block.
    /// </summary>
    [Test]
    public void ShouldRejectDepositLogWithAlteredLayoutWord([Range(0, 2 * DepositEventFieldCount - 1)] int wordIndex)
    {
        byte[] data = CanonicalDepositEventData();
        int wordOffset = wordIndex < DepositEventFieldCount
            ? wordIndex * AbiWordSize
            : ReadDepositEventOffset(data, wordIndex - DepositEventFieldCount);
        WriteDepositEventWord(data, wordOffset, ReadDepositEventWord(data, wordOffset) + 1);

        AssertDepositLogRejected(data);
    }

    /// <summary>
    /// The bytes padding each field out to a whole word carry no meaning, and neither EIP-6110 nor EELS
    /// requires them to be zero, so the deposit must still decode to exactly the same request.
    /// </summary>
    [Test]
    public void ShouldAcceptDepositLogWithNonZeroFieldPadding()
    {
        byte[] data = CanonicalDepositEventData();
        FillDepositEventFieldPadding(data);
        TxReceipt[] txReceipts = [
            Build.A.Receipt.WithLogs(
                CreateDepositLogEntry(data)
            ).TestObject
        ];

        Assert.That(ProcessBlockAndGetRequestsHash(1, txReceipts), Is.EqualTo(
            CalculateHash([TestItem.ExecutionRequestA], _executionWithdrawalRequests, _executionConsolidationRequests)
        ));
    }

    /// <summary>
    /// The payload every layout case starts from: the encoder output that <see cref="ShouldProcessExecutionRequests"/>
    /// proves is accepted, so each case differs from a valid deposit log only by its own mutation.
    /// </summary>
    private static byte[] CanonicalDepositEventData() =>
        EncodeDepositEventData(TestItem.ExecutionRequestA.RequestDataParts);

    private void AssertDepositLogRejected(byte[] data)
    {
        TxReceipt[] txReceipts = [
            Build.A.Receipt.WithLogs(
                CreateDepositLogEntry(data)
            ).TestObject
        ];

        InvalidBlockException exception = Assert.Throws<InvalidBlockException>(() => ProcessBlockAndGetRequestsHash(1, txReceipts))!;
        Assert.That(exception.Message, Does.StartWith(BlockErrorMessages.InvalidDepositEventLayout(string.Empty)));
    }

    /// <summary>
    /// Rewrites canonically encoded deposit event data into a layout EIP-6110 does not allow, returning the
    /// payload to use. The argument may be mutated in place, so pass a freshly encoded array.
    /// </summary>
    private static byte[] ApplyDepositEventLayoutMutation(byte[] data, DepositEventLayoutMutation mutation)
    {
        switch (mutation)
        {
            case DepositEventLayoutMutation.SwappedPubkeyAndCredentialsBlocks:
                return SwapDepositEventBlockWithNext(data, PubkeyHeadWord);
            case DepositEventLayoutMutation.SwappedAmountAndSignatureBlocks:
                return SwapDepositEventBlockWithNext(data, AmountHeadWord);
            case DepositEventLayoutMutation.BlocksShiftedByOneWord:
                return ShiftDepositEventBlocksByOneWord(data);
            case DepositEventLayoutMutation.OffsetBeyondData:
                WriteDepositEventOffset(data, PubkeyHeadWord, int.MaxValue);
                return data;
            case DepositEventLayoutMutation.OffsetHighBitsSet:
                data[0] = 1;
                return data;
            case DepositEventLayoutMutation.ExtraTrailingWord:
                Array.Resize(ref data, data.Length + AbiWordSize);
                return data;
            case DepositEventLayoutMutation.TruncatedData:
                return data[..^AbiWordSize];
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    /// <summary>
    /// Repacks the block at <paramref name="headWord"/> and the one after it in the opposite order. Every
    /// field still decodes to the right size and the data length is unchanged, so only the two head offsets
    /// stop being canonical — the shape a generic ABI decoder accepts and the spec rejects.
    /// </summary>
    private static byte[] SwapDepositEventBlockWithNext(byte[] data, int headWord)
    {
        int firstOffset = ReadDepositEventOffset(data, headWord);
        int secondOffset = ReadDepositEventOffset(data, headWord + 1);
        byte[] firstBlock = data[firstOffset..secondOffset];
        byte[] secondBlock = data[secondOffset..DepositEventBlockEnd(data, headWord + 1)];

        secondBlock.CopyTo(data.AsSpan(firstOffset));
        firstBlock.CopyTo(data.AsSpan(firstOffset + secondBlock.Length));
        WriteDepositEventOffset(data, headWord, firstOffset + secondBlock.Length);
        WriteDepositEventOffset(data, headWord + 1, firstOffset);

        return data;
    }

    /// <summary>
    /// Inserts a word of padding between the head and the field blocks, the shape an encoder that does not
    /// pack tightly would emit. Every field still decodes to the right size, but the data grows by a word.
    /// </summary>
    private static byte[] ShiftDepositEventBlocksByOneWord(byte[] data)
    {
        const int headLength = DepositEventFieldCount * AbiWordSize;
        byte[] shifted = new byte[data.Length + AbiWordSize];
        data.AsSpan(0, headLength).CopyTo(shifted);
        data.AsSpan(headLength).CopyTo(shifted.AsSpan(headLength + AbiWordSize));

        for (int headWord = 0; headWord < DepositEventFieldCount; headWord++)
        {
            WriteDepositEventOffset(shifted, headWord, ReadDepositEventOffset(shifted, headWord) + AbiWordSize);
        }

        return shifted;
    }

    private static void FillDepositEventFieldPadding(byte[] data)
    {
        for (int headWord = 0; headWord < DepositEventFieldCount; headWord++)
        {
            int offset = ReadDepositEventOffset(data, headWord);
            int paddingStart = offset + AbiWordSize + ReadDepositEventWord(data, offset);
            data.AsSpan(paddingStart, DepositEventBlockEnd(data, headWord) - paddingStart).Fill(0xFF);
        }
    }

    private static int DepositEventBlockEnd(byte[] data, int headWord) =>
        headWord + 1 < DepositEventFieldCount ? ReadDepositEventOffset(data, headWord + 1) : data.Length;

    private static int ReadDepositEventOffset(byte[] data, int headWord) =>
        ReadDepositEventWord(data, headWord * AbiWordSize);

    private static void WriteDepositEventOffset(byte[] data, int headWord, int offset) =>
        WriteDepositEventWord(data, headWord * AbiWordSize, offset);

    private static int ReadDepositEventWord(byte[] data, int wordOffset) =>
        BinaryPrimitives.ReadInt32BigEndian(WordTail(data, wordOffset));

    private static void WriteDepositEventWord(byte[] data, int wordOffset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(WordTail(data, wordOffset), value);

    private static Span<byte> WordTail(byte[] data, int wordOffset) =>
        data.AsSpan(wordOffset + AbiWordSize - sizeof(int), sizeof(int));

    public enum DepositEventLayoutMutation
    {
        SwappedPubkeyAndCredentialsBlocks,
        SwappedAmountAndSignatureBlocks,
        BlocksShiftedByOneWord,
        OffsetBeyondData,
        OffsetHighBitsSet,
        ExtraTrailingWord,
        TruncatedData
    }

    [Test]
    public void ShouldUseCorrectDepositTopic() => Assert.That(ExecutionRequestsProcessor.DepositEventAbi.Hash, Is.EqualTo(new Hash256("0x649bbc62d0e31342afea4e5cd82d4049e7e1ee912fc0889aa790803be39038c5")));
}
