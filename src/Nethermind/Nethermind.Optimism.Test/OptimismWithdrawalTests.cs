// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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
    public void Withdrawals_Processing(ulong timestamp, Hash256? withdrawalHash)
    {
        using var db = new MemDb();
        using var store = new TrieStore(db, TestLogManager.Instance);

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

        var processor = new OptimismWithdrawals.Processor(state, TestLogManager.Instance, Spec.Instance);
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
}
