// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain;
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
using Nethermind.Monitoring;

namespace Nethermind.HealthChecks.Test
{
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
            IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
            ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
            IHealthHintService healthHintService = Substitute.For<IHealthHintService>();
            INethermindApi api = Substitute.For<INethermindApi>();
            api.SpecProvider = Substitute.For<ISpecProvider>();
            blockchainProcessor.IsProcessingBlocks(Arg.Any<ulong?>()).Returns(test.IsProcessingBlocks);
            blockProducer.IsProducingBlocks(Arg.Any<ulong?>()).Returns(test.IsProducingBlocks);
            syncServer.GetPeerCount().Returns(test.PeerCount);

            IDriveInfo drive = Substitute.For<IDriveInfo>();
            drive.AvailableFreeSpace.Returns(_freeSpaceBytes);
            drive.TotalSize.Returns((long)(_freeSpaceBytes * 100.0 / test.AvailableDiskSpacePercent));
            drive.RootDirectory.FullName.Returns("C:/");

            BlockHeaderBuilder GetBlockHeader(int blockNumber) => Build.A.BlockHeader.WithNumber(blockNumber);
            blockFinder.Head.Returns(new Block(GetBlockHeader(4).TestObject));
            if (test.IsSyncing)
            {
                blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(15).TestObject);
            }
            else
            {
                blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(2).TestObject);
            }

            IEthSyncingInfo ethSyncingInfo = new EthSyncingInfo(blockFinder, receiptStorage, syncConfig, LimboLogs.Instance);
            NodeHealthService nodeHealthService =
                new(syncServer, blockchainProcessor, blockProducer, new HealthChecksConfig(),
                    healthHintService, ethSyncingInfo, new EngineRpcCapabilitiesProvider(api.SpecProvider), api, new[] { drive }, test.IsMining);
            CheckHealthResult result = nodeHealthService.CheckHealth();
            Assert.AreEqual(test.ExpectedHealthy, result.Healthy);
            Assert.AreEqual(test.ExpectedMessage, FormatMessages(result.Messages.Select(x => x.Message)));
            Assert.AreEqual(test.ExpectedLongMessage, FormatMessages(result.Messages.Select(x => x.LongMessage)));
        }

        [Test]
        public void post_merge_health_checks([ValueSource(nameof(CheckHealthPostMergeTestCases))] CheckHealthPostMergeTest test)
        {
            Assert.AreEqual(test.EnabledCapabilities.Length, test.EnabledCapabilitiesUpdatedCalls.Length);
            Assert.AreEqual(test.DisabledCapabilities.Length, test.DisabledCapabilitiesUpdatedCalls.Length);

            IBlockTree blockFinder = Substitute.For<IBlockTree>();
            ISyncServer syncServer = Substitute.For<ISyncServer>();
            IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
            IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
            IHealthHintService healthHintService = Substitute.For<IHealthHintService>();
            INethermindApi api = Substitute.For<INethermindApi>();

            ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
            api.Timestamper.Returns(timestamper);
            api.JsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();

            MethodStats[] enabledMethodStats = new MethodStats[test.EnabledCapabilities.Length];
            for (int i = 0; i < enabledMethodStats.Length; i++)
            {
                enabledMethodStats[i] = new MethodStats();
                api.JsonRpcLocalStats!.GetMethodStats(test.EnabledCapabilities[i]).Returns(enabledMethodStats[i]);
            }

            MethodStats[] disabledMethodStats = new MethodStats[test.DisabledCapabilities.Length];
            for (int i = 0; i < disabledMethodStats.Length; i++)
            {
                disabledMethodStats[i] = new MethodStats();
                api.JsonRpcLocalStats!.GetMethodStats(test.DisabledCapabilities[i]).Returns(disabledMethodStats[i]);
            }

            syncServer.GetPeerCount().Returns(test.PeerCount);
            IDriveInfo drive = Substitute.For<IDriveInfo>();
            drive.AvailableFreeSpace.Returns(_freeSpaceBytes);
            drive.TotalSize.Returns((long)(_freeSpaceBytes * 100.0 / test.AvailableDiskSpacePercent));
            drive.RootDirectory.FullName.Returns("C:/");

            api.SpecProvider = Substitute.For<ISpecProvider>();
            api.SpecProvider.TerminalTotalDifficulty.Returns(UInt256.Zero);

            BlockHeaderBuilder GetBlockHeader(int blockNumber) => Build.A.BlockHeader.WithNumber(blockNumber);

            blockFinder.Head.Returns(new Block(GetBlockHeader(4).WithDifficulty(0).TestObject));
            if (test.IsSyncing)
            {
                blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(15).TestObject);
            }
            else
            {
                blockFinder.FindBestSuggestedHeader().Returns(GetBlockHeader(2).TestObject);
            }

            CustomRpcCapabilitiesProvider customProvider =
                new(test.EnabledCapabilities, test.DisabledCapabilities);
            IEthSyncingInfo ethSyncingInfo = new EthSyncingInfo(blockFinder, new InMemoryReceiptStorage(), new SyncConfig(), new TestLogManager());
            NodeHealthService nodeHealthService =
                new(syncServer, blockchainProcessor, blockProducer, new HealthChecksConfig(),
                    healthHintService, ethSyncingInfo, customProvider, api, new[] { drive }, false);
            nodeHealthService.CheckHealth();

            timestamper.Add(TimeSpan.FromSeconds(test.TimeSpanSeconds));
            for (int i = 0; i < enabledMethodStats.Length; i++)
            {
                enabledMethodStats[i].Successes = test.EnabledCapabilitiesUpdatedCalls[i];
            }

            for (int i = 0; i < disabledMethodStats.Length; i++)
            {
                disabledMethodStats[i].Successes = test.DisabledCapabilitiesUpdatedCalls[i];
            }

            CheckHealthResult result = nodeHealthService.CheckHealth();
            Assert.AreEqual(test.ExpectedHealthy, result.Healthy);
            Assert.AreEqual(test.ExpectedMessage, FormatMessages(result.Messages.Select(x => x.Message)));
            Assert.AreEqual(test.ExpectedLongMessage, FormatMessages(result.Messages.Select(x => x.LongMessage)));
        }

        public class CheckHealthPostMergeTest
        {
            public int Lp { get; set; }
            public int PeerCount { get; set; }

            public bool IsSyncing { get; set; }

            public bool ExpectedHealthy { get; set; }

            public string ExpectedMessage { get; set; }

            public string ExpectedLongMessage { get; set; }

            public int[] EnabledCapabilitiesUpdatedCalls { get; set; }

            public int[] DisabledCapabilitiesUpdatedCalls { get; set; } = Array.Empty<int>();

            public string[] EnabledCapabilities { get; set; }

            public string[] DisabledCapabilities { get; set; } = Array.Empty<string>();

            public int TimeSpanSeconds { get; set; }
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
                    ExpectedMessage = "Fully synced. Node is not connected to any peers.",
                    ExpectedLongMessage = "The node is now fully synced with a network. Node is not connected to any peers."
                };
                yield return new CheckHealthTest()
                {
                    Lp = 3,
                    IsSyncing = true,
                    PeerCount = 7,
                    ExpectedHealthy = false,
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
                    ExpectedHealthy = false,
                    ExpectedMessage = "Fully synced. Peers: 10. No messages from CL.",
                    TimeSpanSeconds = 301,
                    EnabledCapabilities = new[] { "A", "B", "C" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 0, 0, 0 },
                    ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10. No new messages from CL after last check."
                };
                yield return new CheckHealthPostMergeTest()
                {
                    Lp = 2,
                    IsSyncing = false,
                    PeerCount = 10,
                    ExpectedHealthy = true,
                    ExpectedMessage = "Fully synced. Peers: 10.",
                    TimeSpanSeconds = 15,
                    EnabledCapabilities = new[] { "A", "B", "C" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 0, 0, 0 },
                    ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
                };
                yield return new CheckHealthPostMergeTest()
                {
                    Lp = 3,
                    IsSyncing = false,
                    PeerCount = 10,
                    ExpectedHealthy = true,
                    ExpectedMessage = "Fully synced. Peers: 10.",
                    TimeSpanSeconds = 301,
                    EnabledCapabilities = new[] { "A", "B", "C" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 1, 1, 1 },
                    ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
                };
                yield return new CheckHealthPostMergeTest()
                {
                    Lp = 4,
                    IsSyncing = false,
                    PeerCount = 10,
                    ExpectedHealthy = true,
                    ExpectedMessage = "Fully synced. Peers: 10.",
                    TimeSpanSeconds = 15,
                    EnabledCapabilities = new[] { "A", "B", "C" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 1, 1, 1 },
                    ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
                };
                yield return new CheckHealthPostMergeTest()
                {
                    Lp = 4,
                    IsSyncing = false,
                    PeerCount = 10,
                    ExpectedHealthy = false,
                    ExpectedMessage = "Fully synced. Peers: 10. No messages from CL.",
                    TimeSpanSeconds = 301,
                    EnabledCapabilities = new[] { "A", "B", "C" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 0, 0, 0 },
                    DisabledCapabilities = new[] { "X", "Y", "Z" },
                    DisabledCapabilitiesUpdatedCalls = new[] { 1, 1, 1 },
                    ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10. No new messages from CL after last check."
                };
                yield return new CheckHealthPostMergeTest()
                {
                    Lp = 4,
                    IsSyncing = false,
                    PeerCount = 10,
                    ExpectedHealthy = true,
                    ExpectedMessage = "Fully synced. Peers: 10.",
                    TimeSpanSeconds = 301,
                    EnabledCapabilities = new[] { "A", "B", "C" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 0, 1, 0 },
                    DisabledCapabilities = new[] { "X", "Y", "Z" },
                    DisabledCapabilitiesUpdatedCalls = new[] { 0, 0, 0 },
                    ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
                };
                yield return new CheckHealthPostMergeTest()
                {
                    Lp = 5,
                    IsSyncing = true,
                    PeerCount = 10,
                    ExpectedHealthy = false,
                    ExpectedMessage = "Still syncing. Peers: 10.",
                    TimeSpanSeconds = 301,
                    EnabledCapabilities = new[] { "A", "B", "C" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 1, 1, 1 },
                    ExpectedLongMessage = "The node is still syncing, CurrentBlock: 4, HighestBlock: 15. The status will change to healthy once synced. Peers: 10."
                };
                yield return new CheckHealthPostMergeTest()
                {
                    Lp = 5,
                    IsSyncing = false,
                    PeerCount = 10,
                    ExpectedHealthy = true,
                    ExpectedMessage = "Fully synced. Peers: 10.",
                    TimeSpanSeconds = 301,
                    EnabledCapabilities = new[] { "engine_forkchoiceUpdatedV999", "engine_newPayloadV999" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 1, 1 },
                    ExpectedLongMessage = "The node is now fully synced with a network. Peers: 10."
                };
                yield return new CheckHealthPostMergeTest()
                {
                    Lp = 6,
                    IsSyncing = false,
                    PeerCount = 10,
                    ExpectedHealthy = false,
                    ExpectedMessage = "Fully synced. Peers: 10. Low free disk space.",
                    TimeSpanSeconds = 15,
                    EnabledCapabilities = new[] { "A", "B", "C" },
                    EnabledCapabilitiesUpdatedCalls = new[] { 1, 1, 1 },
                    AvailableDiskSpacePercent = 4.73,
                    ExpectedLongMessage = $"The node is now fully synced with a network. Peers: 10. The node is running out of free disk space in 'C:/' - only {1.50:F2} GB ({4.73:F2}%) left."
                };
            }
        }

        private static string FormatMessages(IEnumerable<string> messages)
        {
            if (messages.Any(x => !string.IsNullOrWhiteSpace(x)))
            {
                var joined = string.Join(". ", messages.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(joined))
                {
                    return joined + ".";
                }
            }

            return string.Empty;
        }

        private class CustomRpcCapabilitiesProvider : IRpcCapabilitiesProvider
        {
            private readonly Dictionary<string, bool> _capabilities = new();

            public CustomRpcCapabilitiesProvider(IReadOnlyList<string> enabledCapabilities, IReadOnlyList<string> disabledCapabilities)
            {
                foreach (string capability in enabledCapabilities)
                {
                    _capabilities[capability] = true;
                }

                foreach (string capability in disabledCapabilities)
                {
                    _capabilities[capability] = false;
                }
            }

            public IReadOnlyDictionary<string, bool> GetEngineCapabilities()
            {
                return _capabilities;
            }
        }
    }
}
