// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
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
        using var db = new MemDb();
        using var store = TestTrieStoreFactory.Build(db, TestLogManager.Instance);

        var state = new WorldState(store, NullDb.Instance, TestLogManager.Instance);

        var genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithTimestamp(Spec.GenesisTimestamp)
            .TestObject;

        var header = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(timestamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString("0x00ffffffffffffffff"))
            .WithWithdrawalsRoot(withdrawalHash)
            .TestObject;

        var releaseSpec = Substitute.For<IReleaseSpec>();
        var block = Build.A.Block
            .WithHeader(header)
            .WithTransactions(0, releaseSpec)
            .TestObject;

        state.CreateAccount(PreDeploys.L2ToL1MessagePasser, 1, 1);

        // This will make the storage root of ActualStorageRoot
        state.Set(new StorageCell(PreDeploys.L2ToL1MessagePasser, UInt256.One), [1]);

        var processor = new OptimismWithdrawalProcessor(state, TestLogManager.Instance, Spec.Instance);
        processor.ProcessWithdrawals(block, releaseSpec);

        if (withdrawalHash == null)
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
        using var db = new MemDb();
        using var store = TestTrieStoreFactory.Build(db, TestLogManager.Instance);

        var state = new WorldState(store, NullDb.Instance, TestLogManager.Instance);
        var processor = new OptimismWithdrawalProcessor(state, TestLogManager.Instance, Spec.Instance);
        var releaseSpec = Substitute.For<IReleaseSpec>();

        // Initialize the storage root
        state.CreateAccount(PreDeploys.L2ToL1MessagePasser, 1, 1);
        state.Set(new StorageCell(PreDeploys.L2ToL1MessagePasser, UInt256.One), [10]);
        state.Commit(releaseSpec);

        var header_A = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(Spec.IsthmusTimeStamp)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString("0x00ffffffffffffffff"))
            .TestObject;

        var block_A = Build.A.Block
            .WithHeader(header_A)
            .WithTransactions(0, releaseSpec)
            .TestObject;

        processor.ProcessWithdrawals(block_A, releaseSpec);
        block_A.WithdrawalsRoot.Should().Be(new("0xe11ca0cf3ff4b6b4f02b42f419c244e0ed4fffac24c14999b2b5bc978c21e652"));

        // Modify the storage root
        state.Set(new StorageCell(PreDeploys.L2ToL1MessagePasser, UInt256.One), [20]);

        var header_B = Build.A.BlockHeader
            .WithNumber(2)
            .WithTimestamp(Spec.IsthmusTimeStamp + 2)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(Bytes.FromHexString("0x00ffffffffffffffff"))
            .TestObject;

        var block_B = Build.A.Block
            .WithHeader(header_B)
            .WithTransactions(0, releaseSpec)
            .TestObject;

        processor.ProcessWithdrawals(block_B, releaseSpec);
        block_B.WithdrawalsRoot.Should().Be(new("0x69b9a1b510f62bae4a767b9030b74cacd8e5bef0e5af497f961c642405f5fb62"));
    }
}
