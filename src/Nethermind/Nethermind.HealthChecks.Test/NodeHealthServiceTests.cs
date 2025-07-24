// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Services;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Extensions;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.HealthChecks.Test;

public class NodeHealthServiceTests
{
    private static readonly long _freeSpaceBytes = (int)(1.GiB() * 1.5);

    [Test]
    public void CheckHealth_returns_expected_results([ValueSource(nameof(CheckHealthTestCases))] CheckHealthTest test)
    {
        IBlockTree blockFinder = Substitute.For<IBlockTree>();
        ISyncServer syncServer = Substitute.For<ISyncServer>();
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        IBlockProducerRunner blockProducerRunner = Substitute.For<IBlockProducerRunner>();
        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        IHealthHintService healthHintService = Substitute.For<IHealthHintService>();
        blockchainProcessor.IsProcessingBlocks(Arg.Any<ulong?>()).Returns(test.IsProcessingBlocks);
        blockProducerRunner.IsProducingBlocks(Arg.Any<ulong?>()).Returns(test.IsProducingBlocks);
        syncServer.GetPeerCount().Returns(test.PeerCount);

        IDriveInfo drive = Substitute.For<IDriveInfo>();
        drive.AvailableFreeSpace.Returns(_freeSpaceBytes);
        drive.TotalSize.Returns((long)(_freeSpaceBytes * 100.0 / test.AvailableDiskSpacePercent));
        drive.RootDirectory.FullName.Returns("C:/");

        static BlockHeaderBuilder GetBlockHeader(int blockNumber) => Build.A.BlockHeader.WithNumber(blockNumber);
        blockFinder.Head.Returns(new Block(GetBlockHeader(4).TestObject));
        if (test.IsSyncing)
        {
            blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(15).TestObject);
        }
        else
        {
            blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(2).TestObject);
        }

        IEthSyncingInfo ethSyncingInfo = new EthSyncingInfo(blockFinder, Substitute.For<ISyncPointers>(), syncConfig, Substitute.For<ISyncModeSelector>(), Substitute.For<ISyncProgressResolver>(), LimboLogs.Instance);
        IClHealthTracker tracker = Substitute.For<IClHealthTracker>();
        NodeHealthService nodeHealthService =
            new(syncServer, blockchainProcessor, blockProducerRunner, new HealthChecksConfig(),
                healthHintService, ethSyncingInfo, tracker, null, new[] { drive }, test.IsMining);
        CheckHealthResult result = nodeHealthService.CheckHealth();
        Assert.That(result.Healthy, Is.EqualTo(test.ExpectedHealthy));
        Assert.That(FormatMessages(result.Messages.Select(static x => x.Message)), Is.EqualTo(test.ExpectedMessage));
        Assert.That(FormatMessages(result.Messages.Select(static x => x.LongMessage)), Is.EqualTo(test.ExpectedLongMessage));
        Assert.That(result.IsSyncing, Is.EqualTo(test.IsSyncing));
        Assert.That(test.ExpectedErrors, Is.EqualTo(result.Errors).AsCollection);
    }

    [Test]
    public void post_merge_health_checks([ValueSource(nameof(CheckHealthPostMergeTestCases))] CheckHealthPostMergeTest test)
    {
        IBlockTree blockFinder = Substitute.For<IBlockTree>();
        ISyncServer syncServer = Substitute.For<ISyncServer>();
        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        IBlockProducerRunner blockProducerRunner = Substitute.For<IBlockProducerRunner>();
        IHealthHintService healthHintService = Substitute.For<IHealthHintService>();
        ISyncModeSelector syncModeSelector = new StaticSelector(test.SyncMode);
        syncServer.GetPeerCount().Returns(test.PeerCount);
        IDriveInfo drive = Substitute.For<IDriveInfo>();
        drive.AvailableFreeSpace.Returns(_freeSpaceBytes);
        drive.TotalSize.Returns((long)(_freeSpaceBytes * 100.0 / test.AvailableDiskSpacePercent));
        drive.RootDirectory.FullName.Returns("C:/");

        static BlockHeaderBuilder GetBlockHeader(int blockNumber) => Build.A.BlockHeader.WithNumber(blockNumber);

        blockFinder.Head.Returns(new Block(GetBlockHeader(4).WithDifficulty(0).TestObject));
        if (test.IsSyncing)
        {
            blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(15).TestObject);
        }
        else
        {
            blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(2).TestObject);
        }

        IEthSyncingInfo ethSyncingInfo = new EthSyncingInfo(blockFinder, Substitute.For<ISyncPointers>(),
            new SyncConfig(), syncModeSelector, Substitute.For<ISyncProgressResolver>(), new TestLogManager());
        IClHealthTracker tracker = Substitute.For<IClHealthTracker>();
        tracker.CheckClAlive().Returns(test.ClAlive);
        NodeHealthService nodeHealthService =
            new(syncServer, blockchainProcessor, blockProducerRunner, new HealthChecksConfig(),
                healthHintService, ethSyncingInfo, tracker, UInt256.Zero, new[] { drive }, false);

        CheckHealthResult result = nodeHealthService.CheckHealth();
        Assert.That(result.Healthy, Is.EqualTo(test.ExpectedHealthy));
        Assert.That(FormatMessages(result.Messages.Select(static x => x.Message)), Is.EqualTo(test.ExpectedMessage));
        Assert.That(FormatMessages(result.Messages.Select(static x => x.LongMessage)), Is.EqualTo(test.ExpectedLongMessage));
        Assert.That(result.IsSyncing, Is.EqualTo(test.IsSyncing));
        Assert.That(test.ExpectedErrors, Is.EqualTo(result.Errors).AsCollection);
    }

    public class CheckHealthPostMergeTest
    {
        public int Lp { get; set; }
        public int PeerCount { get; set; }
        public bool IsSyncing { get; set; }
        public bool ExpectedHealthy { get; set; }
        public string ExpectedMessage { get; set; }
        public string ExpectedLongMessage { get; set; }
        public string[] ExpectedErrors { get; set; }
        public bool ClAlive { get; set; } = true;
        public SyncMode SyncMode { get; set; }
        public double AvailableDiskSpacePercent { get; set; } = 11;

        public override string ToString() =>
            $"Lp: {Lp} ExpectedHealthy: {ExpectedHealthy}, ExpectedDescription: {ExpectedMessage}, ExpectedLongDescription: {ExpectedLongMessage}";
    }

    public class CheckHealthTest
    {
        public int Lp { get; set; }
        public int PeerCount { get; set; }
        public bool IsSyncing { get; set; }
        public bool IsMining { get; set; }
        public bool IsProducingBlocks { get; set; }
        public bool IsProcessingBlocks { get; set; }
        public double AvailableDiskSpacePercent { get; set; } = 11;
        public bool ExpectedHealthy { get; set; }
        public string ExpectedMessage { get; set; }
        public string ExpectedLongMessage { get; set; }
        public List<string> ExpectedErrors { get; set; }

        public override string ToString() =>
            $"Lp: {Lp} ExpectedHealthy: {ExpectedHealthy}, ExpectedDescription: {ExpectedMessage}, ExpectedLongDescription: {ExpectedLongMessage}";
    }

    public static IEnumerable<CheckHealthTest> CheckHealthTestCases
    {
        get
        {
            yield return new CheckHealthTest()
            {
                Lp = 1,
                IsSyncing = false,
                IsProcessingBlocks = true,
                PeerCount = 10,
                ExpectedHealthy = true,
                ExpectedErrors = new(),
                ExpectedMessage = "Fully synced. Peers: 10.",
                ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 10."
            };
            yield return new CheckHealthTest()
            {
                Lp = 2,
                IsSyncing = false,
                IsProcessingBlocks = true,
                PeerCount = 0,
                ExpectedHealthy = false,
                ExpectedErrors = new() { "NoPeers" },
                ExpectedMessage = "Fully synced. Node is not connected to any peers.",
                ExpectedLongMessage = "The node is now fully synced with a network. Node is not connected to any peers."
            };
            yield return new CheckHealthTest()
            {
                Lp = 3,
                IsSyncing = true,
                PeerCount = 7,
                ExpectedHealthy = false,
                ExpectedErrors = new(),
                ExpectedMessage = "Still syncing. Peers: 7.",
                ExpectedLongMessage = $"The node is still syncing, CurrentBlock: 4, HighestBlock: 15. The status will change to healthy once synced. Peers: 7."
            };
            yield return new CheckHealthTest()
            {
                Lp = 4,
                IsSyncing = false,
                IsProcessingBlocks = false,
                PeerCount = 7,
                ExpectedHealthy = false,
                ExpectedErrors = new() { "NotProcessingBlocks" },
                ExpectedMessage = "Fully synced. Peers: 7. Stopped processing blocks.",
                ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 7. The node stopped processing blocks."
            };
            yield return new CheckHealthTest()
            {
                Lp = 5,
                IsSyncing = true,
                IsMining = true,
                IsProducingBlocks = false,
                IsProcessingBlocks = false,
                PeerCount = 4,
                ExpectedHealthy = true,
                ExpectedErrors = new(),
                ExpectedMessage = "Still syncing. Peers: 4.",
                ExpectedLongMessage = $"The node is still syncing, CurrentBlock: 4, HighestBlock: 15. The status will change to healthy once synced. Peers: 4."
            };
            yield return new CheckHealthTest()
            {
                Lp = 6,
                IsSyncing = true,
                IsMining = true,
                IsProducingBlocks = false,
                IsProcessingBlocks = false,
                PeerCount = 0,
                ExpectedHealthy = false,
                ExpectedErrors = new() { "NoPeers" },
                ExpectedMessage = "Still syncing. Node is not connected to any peers.",
                ExpectedLongMessage = "The node is still syncing, CurrentBlock: 4, HighestBlock: 15. The status will change to healthy once synced. Node is not connected to any peers."
            };
            yield return new CheckHealthTest()
            {
                Lp = 7,
                IsSyncing = false,
                IsMining = true,
                IsProducingBlocks = false,
                IsProcessingBlocks = false,
                PeerCount = 1,
                ExpectedHealthy = false,
                ExpectedErrors = new() { "NotProcessingBlocks", "NotProducingBlocks" },
                ExpectedMessage = "Fully synced. Peers: 1. Stopped processing blocks. Stopped producing blocks.",
                ExpectedLongMessage = "The node is now fully synced with a network. Peers: 1. The node stopped processing blocks. The node stopped producing blocks."
            };
            yield return new CheckHealthTest()
            {
                Lp = 8,
                IsSyncing = false,
                IsMining = true,
                IsProducingBlocks = true,
                IsProcessingBlocks = true,
                PeerCount = 1,
                ExpectedHealthy = true,
                ExpectedErrors = new(),
                ExpectedMessage = "Fully synced. Peers: 1.",
                ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 1."
            };
            yield return new CheckHealthTest()
            {
                Lp = 9,
                IsSyncing = false,
                IsMining = true,
                IsProducingBlocks = true,
                IsProcessingBlocks = true,
                PeerCount = 1,
                AvailableDiskSpacePercent = 4.73,
                ExpectedHealthy = false,
                ExpectedErrors = new() { "LowDiskSpace" },
                ExpectedMessage = "Fully synced. Peers: 1. Low free disk space.",
                ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 1. The node is running out of free disk space in 'C:/' - only {1.5:F2} GB ({4.73:F2}%) left."
            };
        }
    }

    public static IEnumerable<CheckHealthPostMergeTest> CheckHealthPostMergeTestCases
    {
        get
        {
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 1,
                IsSyncing = false,
                PeerCount = 10,
                ClAlive = false,
                ExpectedHealthy = false,
                ExpectedMessage = "Fully synced. Peers: 10. No messages from CL.",
                ExpectedErrors = new[] { "ClUnavailable" },
                ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10. No new messages from CL after last check."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 2,
                IsSyncing = false,
                PeerCount = 10,
                ExpectedHealthy = true,
                ExpectedMessage = "Fully synced. Peers: 10.",
                ExpectedErrors = [],
                ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 3,
                IsSyncing = false,
                PeerCount = 10,
                ExpectedHealthy = true,
                ExpectedMessage = "Fully synced. Peers: 10.",
                ExpectedErrors = [],
                ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 4,
                IsSyncing = false,
                PeerCount = 10,
                ExpectedHealthy = true,
                ExpectedMessage = "Fully synced. Peers: 10.",
                ExpectedErrors = [],
                ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 5,
                IsSyncing = false,
                PeerCount = 10,
                ClAlive = false,
                ExpectedHealthy = false,
                ExpectedMessage = "Fully synced. Peers: 10. No messages from CL.",
                ExpectedErrors = new[] { "ClUnavailable" },
                ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10. No new messages from CL after last check."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 6,
                IsSyncing = false,
                PeerCount = 10,
                ExpectedHealthy = true,
                ExpectedMessage = "Fully synced. Peers: 10.",
                ExpectedErrors = [],
                ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 7,
                IsSyncing = true,
                PeerCount = 10,
                ExpectedHealthy = true,
                ExpectedMessage = "Still syncing. Peers: 10.",
                ExpectedErrors = [],
                ExpectedLongMessage = "The node is still syncing, CurrentBlock: 4, HighestBlock: 15. Peers: 10."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 8,
                IsSyncing = false,
                PeerCount = 10,
                ExpectedHealthy = true,
                ExpectedMessage = "Fully synced. Peers: 10.",
                ExpectedErrors = [],
                ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 9,
                IsSyncing = false,
                PeerCount = 10,
                ExpectedHealthy = false,
                ExpectedMessage = "Fully synced. Peers: 10. Low free disk space.",
                ExpectedErrors = new[] { "LowDiskSpace" },
                AvailableDiskSpacePercent = 4.73,
                ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 10. The node is running out of free disk space in 'C:/' - only {1.50:F2} GB ({4.73:F2}%) left."
            };
            yield return new CheckHealthPostMergeTest()
            {
                Lp = 10,
                IsSyncing = true,
                PeerCount = 10,
                ExpectedHealthy = false,
                ExpectedMessage = "Sync degraded. Peers: 10.",
                ExpectedErrors = new[] { "SyncDegraded" },
                SyncMode = SyncMode.Disconnected,
                ExpectedLongMessage = "Sync degraded(no useful peers), CurrentBlock: 4, HighestBlock: 15. Peers: 10."
            };
        }
    }

    private static string FormatMessages(IEnumerable<string> messages)
    {
        if (messages.Any(static x => !string.IsNullOrWhiteSpace(x)))
        {
            var joined = string.Join(". ", messages.Where(static x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(joined))
            {
                return joined + ".";
            }
        }

        return string.Empty;
    }

    private class CustomRpcCapabilitiesProvider : IRpcCapabilitiesProvider
    {
        private readonly Dictionary<string, (bool Enabled, bool WarnIfMissing)> _capabilities = new();

        public CustomRpcCapabilitiesProvider(IReadOnlyList<string> enabledCapabilities, IReadOnlyList<string> disabledCapabilities)
        {
            foreach (string capability in enabledCapabilities)
            {
                _capabilities[capability] = (true, true);
            }

            foreach (string capability in disabledCapabilities)
            {
                _capabilities[capability] = (false, false);
            }
        }

        public IReadOnlyDictionary<string, (bool Enabled, bool WarnIfMissing)> GetEngineCapabilities()
        {
            return _capabilities;
        }
    }
}
