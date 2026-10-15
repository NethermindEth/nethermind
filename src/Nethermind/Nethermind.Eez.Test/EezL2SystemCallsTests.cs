// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Autofac;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezL2SystemCallsTests
{
    private static readonly Address[] ConsensusAccounts =
        [Eip4788Constants.BeaconRootsAddress, Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7251Constants.ConsolidationRequestPredeployAddress];
    private static readonly byte[] Counter = [0x5f, 0x54, 0x60, 1, 0x01, 0x5f, 0x55, 0x5f, 0x5f, 0xf3];

    [TestCase("ethereum", false, 11)]
    [TestCase("legacy", false, 11)]
    [TestCase("l2", false, 10)]
    [TestCase("l2", true, 10)]
    public void Process_EmptyBlock_PreservesHistoryAndSelectedConsensusRules(string profile, bool missingProofs, int expected)
    {
        UInt256[] counters = Process(profile, missingProofs);
        Assert.That(counters, Is.EqualTo(new UInt256[] { (uint)expected, (uint)expected, (uint)expected }),
            "actual predeploy code writes in the controls but is never read or executed in the selected L2 mode");
    }

    [Test]
    public void Process_LegacyWithoutConsensusProofs_Rejects() =>
        Assert.That(() => Process("legacy", true), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("unwitnessed consensus account"));

    [Test]
    public void Rules_CopyPreservesIdentityAndLeavesLegacyUnchanged()
    {
        using JsonDocument genesis = JsonDocument.Parse("{\"config\":{\"feeCollector\":\"0x0000000000000000000000000000000000000001\"}}");
        EezChainRules original = EezChainRules.FromGenesis(genesis.RootElement);
        EezChainRules selected = original.WithoutConsensusLayerCalls();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((selected.Eezl2Address, selected.FeeCollector, selected.Schedule), Is.EqualTo((original.Eezl2Address, original.FeeCollector, original.Schedule)));
            Assert.That((selected.CallsBeaconRootsContract, selected.ReadsExecutionRequests), Is.EqualTo((false, false)));
            Assert.That((original.CallsBeaconRootsContract, original.ReadsExecutionRequests), Is.EqualTo((true, true)));
        }
    }

    [Test]
    public void Rules_CopyPreservesRollupXSchedule()
    {
        RollupXSchedule schedule = new(2, 6, new RollupXL1Reference(1, 10, TestItem.KeccakA, 100, TestItem.KeccakB), 100);
        EezChainRules original = EezChainRules.RollupX(schedule);
        EezChainRules selected = original.WithoutConsensusLayerCalls();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected.Schedule, Is.SameAs(schedule));
            Assert.That(selected.Eezl2Address, Is.EqualTo(original.Eezl2Address));
            Assert.That(selected.IsRollupX, Is.True);
        }
    }

    private static UInt256 ReadStorage(IWorldState state, Address address)
    {
        state.Get(new StorageCell(address, UInt256.Zero), out UInt256 value);
        return value;
    }

    private static UInt256[] Process(string profile, bool missingProofs)
    {
        IReleaseSpec spec = Osaka.Instance;
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable stateScope = state.BeginScope(IWorldState.PreGenesis);
        foreach (Address account in ConsensusAccounts)
        {
            state.CreateAccount(account, 0, 1);
            state.InsertCode(account, Counter, spec);
            state.Set(new StorageCell(account, UInt256.Zero), 10);
        }
        state.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
        state.InsertCode(Eip2935Constants.BlockHashHistoryAddress, Eip2935Constants.Code, spec);
        state.Commit(spec);
        state.CommitTree(0);
        ContainerBuilder builder = new ContainerBuilder().AddModule(new TestNethermindModule(spec));
        if (profile != "ethereum")
        {
            EezChainRules rules = profile == "l2" ? EezChainRules.Rollup0.WithoutConsensusLayerCalls() : EezChainRules.Rollup0;
            builder.AddModule(new EezModule(new EezConfig(), rules));
        }
        builder.AddSingleton<IBlockValidator>(Always.Valid);
        using IContainer container = builder.Build();
        IWorldState executionState = missingProofs ? new MissingConsensusProofs(state) : state;
        using ILifetimeScope scope = container.BeginLifetimeScope(processing => processing
            .AddSingleton(executionState)
            .AddModule(container.Resolve<IBlockValidationModule[]>()));
        Block block = Build.A.Block.WithNumber(1).WithParentHash(TestItem.KeccakA).WithGasLimit(30_000_000).WithWithdrawals(0).TestObject;
        block.Header.ParentBeaconBlockRoot = Keccak.Zero;
        block.Header.RequestsHash = ExecutionRequestExtensions.EmptyRequestsHash;
        block.Header.IsPostMerge = true;
        (Block processed, TxReceipt[] receipts) = scope.Resolve<IBlockProcessor>().ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, spec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipts, Is.Empty);
            Assert.That(processed.Header.RequestsHash, Is.EqualTo(ExecutionRequestExtensions.EmptyRequestsHash));
            Assert.That(ReadStorage(state, Eip2935Constants.BlockHashHistoryAddress), Is.EqualTo(TestItem.KeccakA.ToUInt256()),
                "the EIP-2935 parent hash update remains active before transactions");
            if (profile == "l2")
            {
                Assert.That(scope.Resolve<IBeaconBlockRootHandler>(), Is.SameAs(NoBeaconRootsCall.Instance));
                Assert.That(scope.Resolve<IRewardCalculator>(), Is.SameAs(NoBlockRewards.Instance));
            }
        }
        UInt256[] result = new UInt256[ConsensusAccounts.Length];
        for (int i = 0; i < result.Length; i++) result[i] = ReadStorage(state, ConsensusAccounts[i]);
        return result;
    }

    private sealed class MissingConsensusProofs(IWorldState state) : WorldStateDecorator(state)
    {
        public override bool AccountExists(Address address)
        {
            if (Array.IndexOf(ConsensusAccounts, address) >= 0) throw new InvalidOperationException("unwitnessed consensus account");
            return base.AccountExists(address);
        }

        public override ref readonly ValueHash256 GetCodeHash(Address address)
        {
            if (Array.IndexOf(ConsensusAccounts, address) >= 0) throw new InvalidOperationException("unwitnessed consensus account");
            return ref base.GetCodeHash(address);
        }
    }
}
