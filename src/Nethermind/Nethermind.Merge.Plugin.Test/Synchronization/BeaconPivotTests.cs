// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

[TestFixture]
public class BeaconPivotTests
{
    private ISyncConfig _syncConfig = null!;

    [SetUp]
    public void Setup()
    {
        _syncConfig = new SyncConfig
        {
            FastSync = true,
            PivotNumber = "1000",
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000"
        };
    }

    [Test]
    public void Beacon_pivot_defaults_to_blocktree_values_when_there_is_no_pivot()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.SyncPivot.Returns((1000, Keccak.Zero));
        IBeaconPivot pivot = new BeaconPivot(new MemDb(), blockTree, AlwaysPoS.Instance, LimboLogs.Instance);
        pivot.PivotHash.Should().Be(Keccak.Zero);
        pivot.PivotNumber.Should().Be(1000);
        pivot.PivotDestinationNumber.Should().Be(0);
    }

    [TestCase(0, 1001)]
    [TestCase(500, 436)]
    public void Beacon_pivot_set_to_pivot_when_set(int processedBlocks, int expectedPivotDestinationNumber)
    {
        IBlockTree blockTree = Build.A.BlockTree()
            .WithSyncConfig(_syncConfig)
            .WithOnlySomeBlocksProcessed(1000, processedBlocks)
            .TestObject;
        IBeaconPivot pivot = new BeaconPivot(new MemDb(), blockTree, AlwaysPoS.Instance, LimboLogs.Instance);

        BlockHeader pivotHeader = blockTree.FindHeader(10, BlockTreeLookupOptions.AllowInvalid)!;
        pivot.EnsurePivot(pivotHeader);
        pivot.PivotHash.Should().Be(pivotHeader.GetOrCalculateHash());
        pivot.PivotNumber.Should().Be(pivotHeader.Number);
        pivot.PivotDestinationNumber.Should().Be(expectedPivotDestinationNumber);
    }
}
