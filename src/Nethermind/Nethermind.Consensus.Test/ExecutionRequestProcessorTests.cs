// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class ExecutionProcessorTests
{
    private ISpecProvider _specProvider;
    private ITransactionProcessor _transactionProcessor;
    private WorldState _stateProvider;
    private IReleaseSpec _spec;
    private static readonly UInt256 AccountBalance = 1.Ether();
    private static readonly Address DepositContractAddress = Eip6110Constants.MainnetDepositContractAddress;
    private static readonly Address eip7002Account = Eip7002Constants.WithdrawalRequestPredeployAddress;
    private static readonly Address eip7251Account = Eip7251Constants.ConsolidationRequestPredeployAddress;
    private static readonly AbiSignature _depositEventABI = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    private static readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;

    private static readonly ExecutionRequest[] _executionDepositRequests = [TestItem.ExecutionRequestA, TestItem.ExecutionRequestB, TestItem.ExecutionRequestC];
    private static readonly ExecutionRequest[] _executionWithdrawalRequests = [TestItem.ExecutionRequestD, TestItem.ExecutionRequestE, TestItem.ExecutionRequestF];
    private static readonly ExecutionRequest[] _executionConsolidationRequests = [TestItem.ExecutionRequestG, TestItem.ExecutionRequestH, TestItem.ExecutionRequestI];

    private void FlatEncodeWithoutType(ExecutionRequest[] requests, Span<byte> buffer)
    {
        int currentPosition = 0;

        foreach (ExecutionRequest request in requests)
        {
            // Ensure the buffer has enough space to accommodate the new data
            if (currentPosition + request.RequestData!.Length > buffer.Length)
            {
                throw new InvalidOperationException("Buffer is not large enough to hold all data of requests");
            }

            // Copy the RequestData to the buffer at the current position
            request.RequestData.CopyTo(buffer.Slice(currentPosition, request.RequestData.Length));
            currentPosition += request.RequestData.Length;
        }
    }

    [SetUp]
    public void Setup()
    {
        _specProvider = MainnetSpecProvider.Instance;
        MemDb stateDb = new();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);

        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        _stateProvider.CreateAccount(eip7002Account, AccountBalance);
        _stateProvider.CreateAccount(eip7251Account, AccountBalance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        _spec = Substitute.For<IReleaseSpec>();

        _spec.RequestsEnabled.Returns(true);
        _spec.DepositsEnabled.Returns(true);
        _spec.WithdrawalRequestsEnabled.Returns(true);
        _spec.ConsolidationRequestsEnabled.Returns(true);

        _spec.DepositContractAddress.Returns(DepositContractAddress);
        _spec.Eip7002ContractAddress.Returns(eip7002Account);
        _spec.Eip7251ContractAddress.Returns(eip7251Account);

        _transactionProcessor = Substitute.For<ITransactionProcessor>();

        _transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<CallOutputTracer>())
            .Returns(ci =>
            {
                Transaction transaction = ci.Arg<Transaction>();
                CallOutputTracer tracer = ci.Arg<CallOutputTracer>();
                if (transaction.To == eip7002Account)
                {
                    Span<byte> buffer = new byte[_executionWithdrawalRequests.GetRequestsByteSize()];
                    FlatEncodeWithoutType(_executionWithdrawalRequests, buffer);
                    tracer.ReturnValue = buffer.ToArray();
                }
                else if (transaction.To == eip7251Account)
                {
                    Span<byte> buffer = new byte[_executionConsolidationRequests.GetRequestsByteSize()];
                    FlatEncodeWithoutType(_executionConsolidationRequests, buffer);
                    tracer.ReturnValue = buffer.ToArray();
                }
                else
                {
                    tracer.ReturnValue = Array.Empty<byte>();
                }
                return new TransactionResult();
            });
    }


    public static Hash256 CalculateHash(
        ExecutionRequest[] depositRequests,
        ExecutionRequest[] withdrawalRequests,
        ExecutionRequest[] consolidationRequests
    )
    {
        using ArrayPoolList<byte[]> requests = ExecutionRequestExtensions.GetFlatEncodedRequests(depositRequests, withdrawalRequests, consolidationRequests);
        return ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(requests.ToArray());
    }

    [Test]
    public void ShouldProcessExecutionRequests()
    {
        Block block = Build.A.Block.TestObject;
        ExecutionRequestsProcessor executionRequestsProcessor = new(_transactionProcessor);

        TxReceipt[] txReceipts = [
            Build.A.Receipt.WithLogs(
                CreateLogEntry(TestItem.ExecutionRequestA.RequestDataParts),
                CreateLogEntry(TestItem.ExecutionRequestB.RequestDataParts),
                CreateLogEntry(TestItem.ExecutionRequestC.RequestDataParts)
            ).TestObject
        ];
        executionRequestsProcessor.ProcessExecutionRequests(block, _stateProvider, txReceipts, _spec);

        Assert.That(block.Header.RequestsHash, Is.EqualTo(
           CalculateHash(_executionDepositRequests, _executionWithdrawalRequests, _executionConsolidationRequests)
       ));


        static LogEntry CreateLogEntry(byte[][] requestDataParts) =>
            Build.A.LogEntry
                .WithData(_abiEncoder.Encode(AbiEncodingStyle.None, _depositEventABI, requestDataParts!))
                .WithAddress(DepositContractAddress).TestObject;
    }
}
