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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

[TestFixture]
public class BeaconHeadersSyncTests
{
    [Test]
    public async Task Can_keep_returning_nulls_after_all_batches_were_prepared()
    {
        IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
        BlockTree blockTree = new(memDbProvider, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb),
            MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            FastBlocks = true,
            PivotNumber = "1000",
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000"
        };
        IBeaconPivot pivot = PreparePivot(2000, syncConfig, blockTree);
        BeaconHeadersSyncFeed feed = new(Substitute.For<ISyncModeSelector>(), blockTree,
            Substitute.For<ISyncPeerPool>(), syncConfig, Substitute.For<ISyncReport>(),
            pivot, new MergeConfig() {Enabled = true}, LimboLogs.Instance);
        feed.InitializeFeed();
        for (int i = 0; i < 6; i++)
        {
            await feed.PrepareRequest();
        }

        HeadersSyncBatch? result = await feed.PrepareRequest();
        result.Should().BeNull();
    }

    [Test]
    public async Task Finishes_when_all_downloaded()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(2000).TestObject);
        ISyncReport report = Substitute.For<ISyncReport>();
        report.HeadersInQueue.Returns(new MeasuredProgress());
        MeasuredProgress measuredProgress = new MeasuredProgress();
        report.BeaconHeaders.Returns(measuredProgress);
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            FastBlocks = true,
            PivotNumber = "1000",
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000"
        };
        IBeaconPivot pivot = PreparePivot(2000, syncConfig, blockTree);
        BeaconHeadersSyncFeed feed = new (Substitute.For<ISyncModeSelector>(), blockTree, Substitute.For<ISyncPeerPool>(), syncConfig, report, pivot, new MergeConfig() {Enabled = true},  LimboLogs.Instance);
        await feed.PrepareRequest();
        blockTree.LowestInsertedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(1001).TestObject);
        HeadersSyncBatch? result = await feed.PrepareRequest();
        result.Should().BeNull();
        feed.CurrentState.Should().Be(SyncFeedState.Finished);
        measuredProgress.HasEnded.Should().BeTrue();
    }

    private IBeaconPivot PreparePivot(long blockNumber, ISyncConfig syncConfig, IBlockTree blockTree)
    {
        IBeaconPivot pivot = new BeaconPivot(syncConfig, new MergeConfig() { Enabled = true }, new MemDb(), blockTree, LimboLogs.Instance);
        BlockHeader pivotHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
        pivot.EnsurePivot(pivotHeader);
        return pivot;
    }
}
