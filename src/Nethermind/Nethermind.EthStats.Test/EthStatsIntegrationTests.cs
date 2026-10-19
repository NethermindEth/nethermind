// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Messages;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using Websocket.Client;
using CoreBlock = Nethermind.Core.Block;
using EthStatsBlock = Nethermind.EthStats.Messages.Models.Block;

namespace Nethermind.EthStats.Test;

public class EthStatsIntegrationTests
{
    [TestCase(3UL, 1UL, 1UL, 3UL, TestName = "NormalizeHistoryRange_swaps_min_and_max")]
    [TestCase(0UL, 100UL, 37UL, 100UL, TestName = "NormalizeHistoryRange_limits_oversized_range")]
    [TestCase(ulong.MaxValue - 63, ulong.MaxValue, ulong.MaxValue - 63, ulong.MaxValue, TestName = "NormalizeHistoryRange_handles_max_ulong_without_overflow")]
    public void NormalizeHistoryRange_handles_edges(
        ulong requestMin,
        ulong requestMax,
        ulong expectedMin,
        ulong expectedMax)
    {
        EthStatsIntegration.NormalizeHistoryRange(
            new EthStatsHistoryRequest(requestMin, requestMax),
            out ulong min,
            out ulong max);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(min, Is.EqualTo(expectedMin));
            Assert.That(max, Is.EqualTo(expectedMax));
        }
    }

    [Test]
    public async Task HandleIncomingMessageAsync_sends_history_in_ascending_order()
    {
        IMessageSender sender = Substitute.For<IMessageSender>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        EthStatsIntegration integration = CreateIntegration(sender, blockTree);
        CoreBlock firstBlock = Build.A.Block.WithNumber(1).TestObject;
        CoreBlock secondBlock = Build.A.Block.WithNumber(2).TestObject;

        blockTree.FindBlock(1UL, BlockTreeLookupOptions.RequireCanonical).Returns(firstBlock);
        blockTree.FindBlock(2UL, BlockTreeLookupOptions.RequireCanonical).Returns(secondBlock);
        sender.SendAsync(Arg.Any<IWebsocketClient>(), Arg.Any<HistoryMessage>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        await integration.HandleIncomingMessageAsync("""{"emit":["history",{"min":1,"max":2}]}""");

        _ = sender.Received(1).SendAsync(
            Arg.Any<IWebsocketClient>(),
            Arg.Is<HistoryMessage>(message => HasHistoryBlocks(message, 1, 2)),
            null);
    }

    [Test]
    public async Task Repeated_disconnects_while_offline_are_logged_at_info_once()
    {
        const string disconnectedMessage = "ETH Stats disconnected";
        Subject<DisconnectionInfo> disconnections = new();
        Subject<ReconnectionInfo> reconnections = new();
        IWebsocketClient websocketClient = Substitute.For<IWebsocketClient>();
        websocketClient.DisconnectionHappened.Returns(disconnections);
        websocketClient.ReconnectionHappened.Returns(reconnections);
        websocketClient.MessageReceived.Returns(Observable.Never<ResponseMessage>());
        IEthStatsClient ethStatsClient = Substitute.For<IEthStatsClient>();
        ethStatsClient.InitAsync().Returns(websocketClient);
        IMessageSender sender = Substitute.For<IMessageSender>();
        sender.SendAsync(Arg.Any<IWebsocketClient>(), Arg.Any<HelloMessage>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        logger.IsDebug.Returns(true);
        using EthStatsIntegration integration = CreateIntegration(sender, Substitute.For<IBlockTree>(), ethStatsClient, new OneLoggerLogManager(new ILogger(logger)));
        await integration.InitAsync();

        for (int i = 0; i < 5; i++)
        {
            disconnections.OnNext(DisconnectionInfo.Create(DisconnectionType.Error, null, null));
        }

        logger.Received(1).Info(Arg.Is<string>(text => text.StartsWith(disconnectedMessage)));
        logger.Received(4).Debug(Arg.Is<string>(text => text.StartsWith(disconnectedMessage)));

        reconnections.OnNext(ReconnectionInfo.Create(ReconnectionType.Initial));
        disconnections.OnNext(DisconnectionInfo.Create(DisconnectionType.Error, null, null));

        logger.Received(2).Info(Arg.Is<string>(text => text.StartsWith(disconnectedMessage)));
    }

    private static EthStatsIntegration CreateIntegration(
        IMessageSender sender,
        IBlockTree blockTree,
        IEthStatsClient ethStatsClient = null,
        ILogManager logManager = null)
        => new(
            "test-node",
            "test-client",
            30303,
            "testnet",
            "eth/68",
            "No",
            "Nethermind/v1",
            "contact",
            true,
            "secret",
            ethStatsClient ?? Substitute.For<IEthStatsClient>(),
            sender,
            Substitute.For<ITxPool>(),
            blockTree,
            Substitute.For<IPeerManager>(),
            Substitute.For<IGasPriceOracle>(),
            Substitute.For<IEthSyncingInfo>(),
            false,
            TimeSpan.FromSeconds(1),
            logManager ?? LimboLogs.Instance);

    private static bool HasHistoryBlocks(HistoryMessage message, ulong firstBlockNumber, ulong secondBlockNumber)
    {
        List<ulong> numbers = [];
        foreach (EthStatsBlock block in message.History)
        {
            numbers.Add(block.Number);
        }

        return numbers.Count == 2 &&
            numbers[0] == firstBlockNumber &&
            numbers[1] == secondBlockNumber;
    }
}
