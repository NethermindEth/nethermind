// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezStatelessExecutorTests
{
    [Test]
    public void Project_ClosedPrefix_MatchesIndependentCleanupWithoutChangingContinuingState([Values] bool funded, [Values] bool metadata)
    {
        EezChainRules rules = ProjectionRules();
        NativeCheckpointProjection projection = new(rules.ProtocolBalanceAccounts, LimboLogs.Instance);
        using Witness witness = EmptyProjectionWitness();
        EezStatelessExecutor executor = new(new TestSpecProvider(Osaka.Instance), rules, LimboLogs.Instance);
        StatelessBlockProcessingEnv env = executor.CreateEnvironment(witness, new EezTransactionProcessorFactory(rules), null, null, projection);
        IWorldState state = env.WorldState;
        Assert.That(state, Is.InstanceOf<StatelessExecutingWorldState>(), "the provider extension must retain mandatory witness bytecode validation");
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        SeedProjectionState(state, funded, metadata);
        Hash256 raw = CommitProjectionState(state);
        Hash256 closed = projection.Project(state, Osaka.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(closed, Is.EqualTo(IndependentlyClosedRoot(rules, funded, metadata)));
            Assert.That(state.StateRoot, Is.EqualTo(raw), "projecting a closed prefix must preserve the continuing draft root");
            Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo((UInt256)(funded ? 7 : 0)));
            Assert.That(state.GetBalance(rules.Eezl2Address), Is.EqualTo((UInt256)(funded ? 5 : 0)));
            Assert.That(state.GetBalance(rules.FeeCollector!), Is.EqualTo((UInt256)23));
            Assert.That(state.GetNonce(rules.Eezl2Address), Is.EqualTo(2UL));
        }
        if (funded)
        {
            state.SubtractFromBalance(rules.Eezl2Address, 5, Osaka.Instance);
            state.AddToBalance(TestItem.AddressA, 5, Osaka.Instance);
            Hash256 continued = CommitProjectionState(state);
            _ = projection.Project(state, Osaka.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(state.StateRoot, Is.EqualTo(continued));
                Assert.That(state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)24), "funds present before the checkpoint remain spendable afterward");
                Assert.That(state.GetBalance(Address.SystemUser), Is.EqualTo((UInt256)7));
            }
        }
    }

    private static Hash256 IndependentlyClosedRoot(EezChainRules rules, bool funded, bool metadata)
    {
        using Witness witness = EmptyProjectionWitness();
        EezStatelessExecutor executor = new(new TestSpecProvider(Osaka.Instance), rules, LimboLogs.Instance);
        IWorldState state = executor.CreateEnvironment(witness).WorldState;
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        SeedProjectionState(state, funded, metadata);
        _ = CommitProjectionState(state);
        if (funded)
        {
            state.SubtractFromBalance(Address.SystemUser, 7, Osaka.Instance);
            state.SubtractFromBalance(rules.Eezl2Address, 5, Osaka.Instance);
        }
        return CommitProjectionState(state);
    }

    private static void SeedProjectionState(IWorldState state, bool funded, bool metadata)
    {
        state.CreateAccount(Address.SystemUser, funded ? (UInt256)7 : UInt256.Zero, metadata ? 3UL : 0UL);
        if (metadata)
        {
            state.InsertCode(Address.SystemUser, new byte[] { 0x00 }, Osaka.Instance);
            state.Set(new StorageCell(Address.SystemUser, UInt256.One), 13);
        }
        Address manager = EezConstants.RollupXEezl2Address;
        state.CreateAccount(manager, funded ? (UInt256)5 : UInt256.Zero, 2);
        state.InsertCode(manager, new byte[] { 0x00 }, Osaka.Instance);
        state.Set(new StorageCell(manager, UInt256.One), 17);
        state.CreateAccount(TestItem.AddressA, 19);
        state.CreateAccount(TestItem.AddressD, 23);
    }

    private static Hash256 CommitProjectionState(IWorldState state)
    {
        state.Commit(Osaka.Instance, commitRoots: true);
        state.RecalculateStateRoot();
        return state.StateRoot;
    }

    private static EezChainRules ProjectionRules() => EezChainRules.NativeRollupX(
        new RollupXSchedule(2, 6, new RollupXL1Reference(1, 99, TestItem.KeccakA, 108, TestItem.KeccakB), 120),
        TestItem.AddressD, NativeSystemTxFormat.ContractV6, 2);

    private static Witness EmptyProjectionWitness() => new()
    {
        Codes = new ArrayPoolList<byte[]>(0),
        State = new ArrayPoolList<byte[]>(0),
        Keys = new ArrayPoolList<byte[]>(0),
        Headers = new ArrayPoolList<byte[]>(0)
    };
}
