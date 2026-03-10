// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismWithdrawalTests
{
    /// <summary>
    /// The value that the actual storage root of <see cref="PreDeploys.L2ToL1MessagePasser"/> will be calculated to.
    /// </summary>
    private static readonly Hash256 ActualStorageRoot = new("0xf38f9f63c760d088d7dd04f743619b6291f63beebd8bdf530628f90e9cfa52d7");

    [TestCaseSource(nameof(WithdrawalsRootData))]
    public void WithdrawalsRoots_Should_Be_Set_According_To_Block_Timestamp(ulong timestamp, Hash256? withdrawalHash)
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = state.BeginScope(IWorldState.PreGenesis);

        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(timestamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString("0x00ffffffffffffffff"))
            .WithWithdrawalsRoot(withdrawalHash)
            .TestObject;

        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
        Block block = Build.A.Block
            .WithHeader(header)
            .WithTransactions(0, releaseSpec)
            .TestObject;

        state.CreateAccount(PreDeploys.L2ToL1MessagePasser, 1, 1);

        // This will make the storage root of ActualStorageRoot
        state.Set(new StorageCell(PreDeploys.L2ToL1MessagePasser, UInt256.One), [1]);

        OptimismWithdrawalProcessor processor = new(state, TestLogManager.Instance, Spec.Instance);
        processor.ProcessWithdrawals(block, releaseSpec);

        if (withdrawalHash is null)
        {
            block.WithdrawalsRoot.Should().BeNull();
        }
        else
        {
            block.WithdrawalsRoot.Should().Be(withdrawalHash);
        }
    }

    private static IEnumerable<TestCaseData> WithdrawalsRootData()
    {
        yield return new TestCaseData(Spec.CanyonTimestamp - 1, null).SetName("Pre Canyon");
        yield return new TestCaseData(Spec.CanyonTimestamp, Keccak.OfAnEmptySequenceRlp).SetName("Post Canyon");
        yield return new TestCaseData(Spec.IsthmusTimeStamp, ActualStorageRoot).SetName("Post Isthmus");
    }

    [Test]
    public void WithdrawalsRoot_IsAlwaysUpToDate_PostIsthmus()
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = state.BeginScope(IWorldState.PreGenesis);
        OptimismWithdrawalProcessor processor = new(state, TestLogManager.Instance, Spec.Instance);
        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();

        // Initialize the storage root
        state.CreateAccount(PreDeploys.L2ToL1MessagePasser, 1, 1);
        state.Set(new StorageCell(PreDeploys.L2ToL1MessagePasser, UInt256.One), [10]);
        state.Commit(releaseSpec);

        BlockHeader headerA = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(Spec.IsthmusTimeStamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString("0x00ffffffffffffffff"))
            .TestObject;

        Block blockA = Build.A.Block
            .WithHeader(headerA)
            .WithTransactions(0, releaseSpec)
            .TestObject;

        processor.ProcessWithdrawals(blockA, releaseSpec);
        blockA.WithdrawalsRoot.Should().Be(new("0xe11ca0cf3ff4b6b4f02b42f419c244e0ed4fffac24c14999b2b5bc978c21e652"));

        // Modify the storage root
        state.Set(new StorageCell(PreDeploys.L2ToL1MessagePasser, UInt256.One), [20]);

        BlockHeader headerB = Build.A.BlockHeader
            .WithNumber(2)
            .WithTimestamp(Spec.IsthmusTimeStamp + 2)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString("0x00ffffffffffffffff"))
            .TestObject;

        Block blockB = Build.A.Block
            .WithHeader(headerB)
            .WithTransactions(0, releaseSpec)
            .TestObject;

        processor.ProcessWithdrawals(blockB, releaseSpec);
        blockB.WithdrawalsRoot.Should().Be(new("0x69b9a1b510f62bae4a767b9030b74cacd8e5bef0e5af497f961c642405f5fb62"));
    }
}
