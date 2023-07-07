// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using System.Collections.Generic;

namespace Nethermind.Evm.Test;

[TestFixture]
internal class TransactionProcessorEip4844Tests
{
    private ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private TransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;

    [SetUp]
    public void Setup()
    {
        MemDb stateDb = new();
        _specProvider = new TestSpecProvider(Cancun.Instance);
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        VirtualMachine virtualMachine = new(TestBlockhashProvider.Instance, _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
    }


    [TestCaseSource(nameof(BalanceIsAffectedByDataGasTestCaseSource))]
    [TestCaseSource(nameof(BalanceIsNotAffectedWhenNotEnoughFunds))]
    public UInt256 Balance_is_affected_by_data_gas_on_execution(UInt256 balance, int blobCount,
        ulong maxFeePerDataGas, ulong excessDataGas, ulong value)
    {
        _stateProvider.CreateAccount(TestItem.AddressA, balance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        long gasLimit = GasCostOf.Transaction;
        Transaction blobTx = Build.A.Transaction
            .WithValue(value)
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithMaxFeePerDataGas(maxFeePerDataGas)
            .WithGasLimit(gasLimit)
            .WithShardBlobTxTypeAndFields(blobCount)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(blobTx)
            .WithGasLimit(gasLimit)
            .WithExcessDataGas(excessDataGas)
            .WithBaseFeePerGas(1)
            .TestObject;

        _transactionProcessor.CallAndRestore(blobTx, block.Header, NullTxTracer.Instance);
        UInt256 deltaBalance = balance - _stateProvider.GetBalance(TestItem.PrivateKeyA.Address);
        Assert.That(deltaBalance, Is.EqualTo(UInt256.Zero));

        _transactionProcessor.Execute(blobTx, block.Header, NullTxTracer.Instance);
        deltaBalance = balance - _stateProvider.GetBalance(TestItem.PrivateKeyA.Address);

        return deltaBalance;
    }

    public static IEnumerable<TestCaseData> BalanceIsAffectedByDataGasTestCaseSource()
    {
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob), 1, 1ul, 0ul, 0ul)
        {
            TestName = "Data gas consumed for 1 blob, minimal balance",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob),
        };
        yield return new TestCaseData(1.Ether(), 1, 1ul, 0ul, 0ul)
        {
            TestName = "Data gas consumed for 1 blob",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob),
        };
        yield return new TestCaseData(1.Ether(), 2, 1ul, 0ul, 0ul)
        {
            TestName = "Data gas consumed for 2 blobs",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + 2 * Eip4844Constants.DataGasPerBlob),
        };
        yield return new TestCaseData(1.Ether(), (int)(Eip4844Constants.MaxDataGasPerTransaction / Eip4844Constants.DataGasPerBlob), 1ul, 0ul, 0ul)
        {
            TestName = "Data gas consumed for max blobs",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.MaxDataGasPerTransaction),
        };
        yield return new TestCaseData(1.Ether(), 1, 10ul, 0ul, 0ul)
        {
            TestName = $"Data gas consumed for 1 blob, with {nameof(Transaction.MaxFeePerDataGas)} more than needed",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob),
        };
        yield return new TestCaseData(1.Ether(), 1, 10ul, (ulong)Eip4844Constants.DataGasUpdateFraction, 0ul)
        {
            TestName = $"Data gas consumed for 1 blob, with data gas price hiking",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob * 2),
        };
        yield return new TestCaseData(1.Ether(), 1, 10ul, (ulong)Eip4844Constants.DataGasUpdateFraction, 2ul)
        {
            TestName = $"Data gas consumed for 1 blob, with data gas price hiking and some {nameof(Transaction.Value)}",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob * 2 + 2),
        };
    }

    public static IEnumerable<TestCaseData> BalanceIsNotAffectedWhenNotEnoughFunds()
    {
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob - 1), 1, 1ul, 0ul, 0ul)
        {
            TestName = $"Rejected if balance is not enough, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob + 41), 1, 1ul, 0ul, 42ul)
        {
            TestName = $"Rejected if balance is not enough to cover {nameof(Transaction.Value)} also, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob), 1, 10ul, (ulong)Eip4844Constants.DataGasUpdateFraction, 0ul)
        {
            TestName = $"Rejected if balance is not enough due to data gas price hiking, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.DataGasPerBlob), 1, 2ul, 0ul, 0ul)
        {
            TestName = $"Rejected if balance does not cover {nameof(Transaction.MaxFeePerDataGas)}, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + 2 * Eip4844Constants.DataGasPerBlob + 41), 1, 2ul, 0ul, 42ul)
        {
            TestName = $"Rejected if balance does not cover {nameof(Transaction.MaxFeePerDataGas)} + {nameof(Transaction.Value)}, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
    }
}
