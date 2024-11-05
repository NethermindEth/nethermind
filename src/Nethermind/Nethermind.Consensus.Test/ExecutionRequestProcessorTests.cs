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
    private static readonly UInt256 AccountBalance = 1.Ether();
    private readonly Address DepositContractAddress = Eip6110Constants.MainnetDepositContractAddress;
    private readonly Address eip7002Account = Eip7002Constants.WithdrawalRequestPredeployAddress;
    private readonly Address eip7251Account = Eip7251Constants.ConsolidationRequestPredeployAddress;
    private IReleaseSpec spec;
    private readonly AbiSignature _depositEventABI = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    private readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;

    ExecutionRequest[] executionDepositRequests = [TestItem.ExecutionRequestA, TestItem.ExecutionRequestB, TestItem.ExecutionRequestC];
    ExecutionRequest[] executionWithdrawalRequests = [TestItem.ExecutionRequestD, TestItem.ExecutionRequestE, TestItem.ExecutionRequestF];
    ExecutionRequest[] executionConsolidationRequests = [TestItem.ExecutionRequestG, TestItem.ExecutionRequestH, TestItem.ExecutionRequestI];

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

        spec = Substitute.For<IReleaseSpec>();

        spec.RequestsEnabled.Returns(true);
        spec.DepositsEnabled.Returns(true);
        spec.WithdrawalRequestsEnabled.Returns(true);
        spec.ConsolidationRequestsEnabled.Returns(true);

        spec.DepositContractAddress.Returns(DepositContractAddress);
        spec.Eip7002ContractAddress.Returns(eip7002Account);
        spec.Eip7251ContractAddress.Returns(eip7251Account);

        _transactionProcessor = Substitute.For<ITransactionProcessor>();

        _transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<CallOutputTracer>())
            .Returns(ci =>
            {
                Transaction transaction = ci.Arg<Transaction>();
                CallOutputTracer tracer = ci.Arg<CallOutputTracer>();
                if (transaction.To == eip7002Account)
                {
                    Span<byte> buffer = new byte[executionWithdrawalRequests.GetRequestsByteSize()];
                    FlatEncodeWithoutType(executionWithdrawalRequests, buffer);
                    tracer.ReturnValue = buffer.ToArray();
                }
                else if (transaction.To == eip7251Account)
                {
                    Span<byte> buffer = new byte[executionConsolidationRequests.GetRequestsByteSize()];
                    FlatEncodeWithoutType(executionConsolidationRequests, buffer);
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
                Build.A.LogEntry.WithData(
                    _abiEncoder.Encode(AbiEncodingStyle.None, _depositEventABI, [TestItem.PublicKeyA.Bytes.Slice(0, 48), TestItem.KeccakA.Bytes.ToArray(), BitConverter.GetBytes((ulong)1_000_000_000), TestItem.SignatureBytes, BitConverter.GetBytes((ulong)1)])
                ).WithAddress(DepositContractAddress).TestObject,
                Build.A.LogEntry.WithData(
                    _abiEncoder.Encode(AbiEncodingStyle.None, _depositEventABI, [TestItem.PublicKeyB.Bytes.Slice(0, 48), TestItem.KeccakB.Bytes.ToArray(), BitConverter.GetBytes((ulong)2_000_000_000), TestItem.SignatureBytes, BitConverter.GetBytes((ulong)2)])
                ).WithAddress(DepositContractAddress).TestObject,
                Build.A.LogEntry.WithData(
                    _abiEncoder.Encode(AbiEncodingStyle.None, _depositEventABI, [TestItem.PublicKeyC.Bytes.Slice(0, 48), TestItem.KeccakC.Bytes.ToArray(), BitConverter.GetBytes((ulong)3_000_000_000), TestItem.SignatureBytes, BitConverter.GetBytes((ulong)3)])
                ).WithAddress(DepositContractAddress).TestObject
            ).TestObject
        ];
        executionRequestsProcessor.ProcessExecutionRequests(block, _stateProvider, txReceipts, spec);

        Assert.That(block.Header.RequestsHash, Is.EqualTo(
           CalculateHash(executionDepositRequests, executionWithdrawalRequests, executionConsolidationRequests)
       ));
    }
}
