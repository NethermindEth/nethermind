//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
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
            FastBlocks = true,
            PivotNumber = "1000",
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000"
        };
    }

    [Test]
    public void Beacon_pivot_defaults_to_sync_config_values_when_there_is_no_pivot()
    {
        IBeaconPivot pivot = new BeaconPivot(_syncConfig, new MemDb(), Substitute.For<IBlockTree>(), LimboLogs.Instance);
        pivot.PivotHash.Should().Be(_syncConfig.PivotHashParsed);
        pivot.PivotNumber.Should().Be(_syncConfig.PivotNumberParsed);
        pivot.PivotDestinationNumber.Should().Be(0);
    }

    [TestCase(0, 1001)]
    [TestCase(500, 436)]
    public void Beacon_pivot_set_to_pivot_when_set(int processedBlocks, int expectedPivotDestinationNumber)
    {
        IBlockTree blockTree = Build.A.BlockTree()
            .WithOnlySomeBlocksProcessed(1000, processedBlocks)
            .TestObject;
        IBeaconPivot pivot = new BeaconPivot(_syncConfig, new MemDb(), blockTree, LimboLogs.Instance);

        BlockHeader pivotHeader = blockTree.FindHeader(10, BlockTreeLookupOptions.AllowInvalid);
        pivot.EnsurePivot(pivotHeader);
        pivot.PivotHash.Should().Be(pivotHeader.Hash);
        pivot.PivotNumber.Should().Be(pivotHeader.Number);
        pivot.PivotDestinationNumber.Should().Be(expectedPivotDestinationNumber);
    }
}
