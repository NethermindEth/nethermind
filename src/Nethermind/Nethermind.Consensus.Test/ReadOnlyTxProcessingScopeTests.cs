// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.State;
using Nethermind.Int256;
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
        Address recipient = Address.FromNumber(1234);
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
            To = Address.FromNumber(1235),
            Data = new byte[] { 0x01 }
        };

        BlockCachePreWarmer.CanSkipEvmWarmup(contractCall, worldState, spec).Should().BeFalse();
        BlockCachePreWarmer.CanSkipEvmWarmup(withData, worldState, spec).Should().BeFalse();
    }

    [Test]
    public void WarmupStorageKeysFromAccessList_reads_all_keys()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using var _ = worldState.BeginScope(IWorldState.PreGenesis);
        AccessList accessList = new AccessList.Builder()
            .AddAddress(Address.FromNumber(10))
            .AddStorage(new UInt256(1))
            .AddStorage(new UInt256(2))
            .AddAddress(Address.FromNumber(11))
            .AddStorage(new UInt256(3))
            .Build();

        Action action = () => BlockCachePreWarmer.WarmupStorageKeysFromAccessList(worldState, accessList);
        action.Should().NotThrow();
    }

    [Test]
    public void WarmupCodeForRecipient_reads_contract_code()
    {
        Address recipient = Address.FromNumber(1236);
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.IsContract(recipient).Returns(true);
        IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec(new ForkActivation(0));

        BlockCachePreWarmer.WarmupCodeForRecipient(worldState, recipient, spec);

        worldState.Received(1).GetCode(recipient);
    }
}
