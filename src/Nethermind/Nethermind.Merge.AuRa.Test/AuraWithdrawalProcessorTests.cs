// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Contracts;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.Specs;
using Nethermind.Specs.GnosisForks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuraWithdrawalProcessorTests
{
    private static readonly Address WithdrawalContractAddress = new("0xbabe2bed00000000000000000000000000000003");

    [Test]
    public void Should_invoke_contract_as_expected()
    {
        IWithdrawalContract contract = Substitute.For<IWithdrawalContract>();
        ILogManager logManager = Substitute.For<ILogManager>();
        AuraWithdrawalProcessor withdrawalProcessor = new(contract, logManager);
        Block block = Build.A.Block
            .WithNumber(123)
            .WithWithdrawals(Build.A.Withdrawal
                    .WithAmount(1_000_000UL)
                    .WithRecipient(Address.SystemUser).TestObject, Build.A.Withdrawal
                    .WithAmount(2_000_000UL)
                    .WithRecipient(Address.Zero).TestObject)
            .TestObject;
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();

        spec.WithdrawalsEnabled.Returns(true);

        // we need to capture those values, because the ArrayPools will be disposed before we can match them
        ulong[] values = [];
        Address[] addresses = [];
        contract.ExecuteWithdrawals(
            block.Header,
            4,
            Arg.Do<IList<ulong>>(a => values = a.ToArray()),
            Arg.Do<IList<Address>>(a => addresses = a.ToArray()));

        withdrawalProcessor.ProcessWithdrawals(block, spec);

        contract.Received(1).ExecuteWithdrawals(
            Arg.Is(block.Header),
            Arg.Is<UInt256>(4),
            Arg.Is<IList<ulong>>(a => values.AsEnumerable().SequenceEqual(new[] { 1_000_000UL, 2_000_000UL })),
            Arg.Is<IList<Address>>(a => addresses.AsEnumerable().SequenceEqual(new[] { Address.SystemUser, Address.Zero })));
    }

    [Test]
    public void Should_not_invoke_contract_before_Shanghai()
    {
        IWithdrawalContract contract = Substitute.For<IWithdrawalContract>();
        ILogManager logManager = Substitute.For<ILogManager>();
        AuraWithdrawalProcessor withdrawalProcessor = new(contract, logManager);
        Block block = Build.A.Block.TestObject;
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();

        spec.WithdrawalsEnabled.Returns(false);

        withdrawalProcessor.ProcessWithdrawals(block, spec);

        contract.Received(0).ExecuteWithdrawals(
            Arg.Any<BlockHeader>(),
            Arg.Any<UInt256>(),
            Arg.Any<IList<ulong>>(),
            Arg.Any<IList<Address>>());
    }

    [Test]
    public void Genesis_withdrawal_contract_call_does_not_create_missing_system_account()
    {
        ReleaseSpec shanghaiSpec = ShanghaiGnosis.Instance.Clone();
        shanghaiSpec.Eip158IgnoredAccount = Address.SystemUser;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(shanghaiSpec, 1, 1)
        {
            SealEngine = SealEngineType.AuRa
        };
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();

        using IDisposable stateScope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(WithdrawalContractAddress, UInt256.Zero);
        byte[] stopCode = [0x00];
        stateProvider.InsertCode(WithdrawalContractAddress, stopCode, shanghaiSpec);
        stateProvider.Commit(shanghaiSpec);
        stateProvider.CommitTree(0);

        Assert.That(stateProvider.AccountExists(Address.SystemUser), Is.False);

        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        ITransactionProcessor transactionProcessor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            stateProvider,
            virtualMachine,
            codeInfoRepository,
            LimboLogs.Instance);
        WithdrawalContract contract = new(transactionProcessor, AbiEncoder.Instance, WithdrawalContractAddress);
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(0)
            .WithGasLimit(0x0206cc80)
            .WithBaseFee(7)
            .WithStateRoot(stateProvider.StateRoot)
            .TestObject;

        Assert.DoesNotThrow(() => contract.ExecuteWithdrawals(header, 4, [], []));
        Assert.That(stateProvider.AccountExists(Address.SystemUser), Is.False);
    }
}
