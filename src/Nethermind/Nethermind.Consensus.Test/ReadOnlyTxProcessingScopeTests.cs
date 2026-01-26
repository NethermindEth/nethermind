// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.State;
using Nethermind.Specs;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class ReadOnlyTxProcessingScopeTests
{
    [Test]
    public void Test_WhenDispose_ThenStateRootWillReset()
    {
        bool closed = false;
        IDisposable closer = new Reactive.AnonymousDisposable(() => closed = true);
        ReadOnlyTxProcessingScope env = new ReadOnlyTxProcessingScope(
            Substitute.For<ITransactionProcessor>(),
            closer,
            Substitute.For<IWorldState>());

        env.Dispose();

        closed.Should().BeTrue();
    }

    [Test]
    public void BuildSenderWarmupPlan_groups_by_sender_and_tracks_offsets()
    {
        Address senderA = Address.FromNumber(1);
        Address senderB = Address.FromNumber(2);
        Transaction[] transactions =
        {
            new Transaction { SenderAddress = senderA },
            new Transaction { SenderAddress = senderB },
            new Transaction { SenderAddress = senderA },
            new Transaction { SenderAddress = senderA },
            new Transaction { SenderAddress = senderB }
        };

        using BlockCachePreWarmer.SenderWarmupPlan plan = BlockCachePreWarmer.BuildSenderWarmupPlan(transactions, includeGroups: true);

        plan.OffsetsArray.Should().NotBeNull();
        plan.OffsetsArray.AsSpan(0, transactions.Length).ToArray().Should().Equal(new[] { 0, 0, 1, 2, 1 });

        plan.SenderGroups.Should().NotBeNull();
        plan.SenderGroups!.Count.Should().Be(2);
        plan.SenderGroups[0].AsSpan().ToArray().Should().Equal(new[] { 0, 2, 3 });
        plan.SenderGroups[1].AsSpan().ToArray().Should().Equal(new[] { 1, 4 });
    }

    [Test]
    public void CanSkipEvmWarmup_returns_true_for_simple_transfer_to_eoa()
    {
        Address recipient = Address.FromNumber(3);
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.IsContract(recipient).Returns(false);
        IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec(new ForkActivation(0));

        Transaction tx = new()
        {
            To = recipient,
            Data = ReadOnlyMemory<byte>.Empty
        };

        BlockCachePreWarmer.CanSkipEvmWarmup(tx, worldState, spec).Should().BeTrue();
    }

    [Test]
    public void CanSkipEvmWarmup_returns_false_for_contract_call_or_initcode()
    {
        Address recipient = Address.FromNumber(4);
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.IsContract(recipient).Returns(true);
        IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec(new ForkActivation(0));

        Transaction contractCall = new()
        {
            To = recipient,
            Data = ReadOnlyMemory<byte>.Empty
        };

        Transaction withData = new()
        {
            To = Address.FromNumber(5),
            Data = new byte[] { 0x01 }
        };

        BlockCachePreWarmer.CanSkipEvmWarmup(contractCall, worldState, spec).Should().BeFalse();
        BlockCachePreWarmer.CanSkipEvmWarmup(withData, worldState, spec).Should().BeFalse();
    }
}
