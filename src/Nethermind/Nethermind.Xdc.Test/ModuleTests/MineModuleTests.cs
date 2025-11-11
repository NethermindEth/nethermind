// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.TxPool;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using Org.BouncyCastle.Crypto;

namespace Nethermind.Xdc.Test.ModuleTests;
internal class MineModuleTests
{
    private XdcTestBlockchain _blockchainTests;
    private XdcBlockProducer _producer;
    private XdcBlockTree _tree;
    private XdcHotStuff _hotstuff;

    [SetUp]
    public async Task Setup()
    {
        _blockchainTests = await XdcTestBlockchain.Create(useHotStuffModule: true);
        _producer = (XdcBlockProducer)_blockchainTests.BlockProducer;
        _tree = (XdcBlockTree)_blockchainTests.BlockTree;
        _hotstuff = (XdcHotStuff)_blockchainTests.BlockProducerRunner;
    }

    [Test]
    public void YourTurnInitialV2()
    {

    }

    [Test]
    public async Task Should_Mine_Once_Per_Round()
    {
        var parentBlock = _tree.Head!.Header!;
        var masterNodesAddresses = _blockchainTests.MasterNodeCandidates.Select(pv => pv.Address).ToArray();

        // Arrange
        var spec = _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header!);
        var previousLeader = _hotstuff.GetLeaderAddress((XdcBlockHeader)_tree.Head!.Header!, _blockchainTests.XdcContext.CurrentRound, spec);
        Assert.That(previousLeader, Is.Not.Null);

        await _blockchainTests.AddBlock();
        // Wait for mine period to elapse
        await Task.Delay(TimeSpan.FromSeconds(spec.MinePeriod));

        // Act - Attempt to mine again within the same round (the assumption is after mine period current round increments and a new leader is chosen)
        spec = _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header!);
        var newLeader = _hotstuff.GetLeaderAddress((XdcBlockHeader)_tree.Head!.Header!, _blockchainTests.XdcContext.CurrentRound, spec);

        // Assert - Should fail because already mined
        Assert.That(newLeader, Is.Not.Null);

        Assert.That(newLeader, Is.Not.EqualTo(previousLeader));
    }

    /*[Test]
    public async Task Block_Building_Fails_When_Not_Leader()
    {
    }*/
}
