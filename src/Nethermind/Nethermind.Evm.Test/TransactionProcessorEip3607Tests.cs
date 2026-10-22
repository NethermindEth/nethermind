// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class TransactionProcessorEip3607Tests
{
    private ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Prague.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    private static IEnumerable<TestCaseData> SenderCodeCases()
    {
        yield return new TestCaseData(Bytes.Concat(Eip7702Constants.DelegationHeader, TestItem.AddressC.Bytes), TransactionResult.Ok)
        { TestName = "Delegated sender is accepted" };
        yield return new TestCaseData(new byte[] { 0x60, 0x00, 0x60, 0x00, 0xf3 }, TransactionResult.SenderHasDeployedCode)
        { TestName = "Contract sender is rejected" };
        yield return new TestCaseData(Bytes.Concat(Eip7702Constants.DelegationHeader, new byte[19]), TransactionResult.SenderHasDeployedCode)
        { TestName = "Delegation header of wrong length is rejected" };
    }

    [TestCaseSource(nameof(SenderCodeCases))]
    public void Sender_with_code(byte[] code, TransactionResult expected)
    {
        IReleaseSpec spec = _specProvider.GenesisSpec;
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        _stateProvider.InsertCode(TestItem.AddressA, code, spec);
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        Transaction tx = Build.A.Transaction
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithTo(TestItem.AddressB)
            .WithValue(1.GWei)
            .WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10000000).TestObject;

        TransactionResult result = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.That(result, Is.EqualTo(expected));
    }
}
