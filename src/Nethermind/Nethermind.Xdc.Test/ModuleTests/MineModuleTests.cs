// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using Org.BouncyCastle.Crypto;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.ModuleTests;
internal class MineModuleTests
{
    private XdcTestBlockchain _blockchainTests;
    private XdcBlockProducer _producer;
    private XdcBlockTree _tree;
    private XdcHotStuff _hotstuff;

    private const int _masterNodesGenesisCount = 30;
    private const int _prepeparedBlocks = 3;

    [SetUp]
    public async Task Setup()
    {
        _blockchainTests = await XdcTestBlockchain.Create(blocksToAdd: _prepeparedBlocks, useHotStuffModule: true, masterNodesCount: _masterNodesGenesisCount);
        _producer = (XdcBlockProducer)_blockchainTests.BlockProducer;
        _tree = (XdcBlockTree)_blockchainTests.BlockTree;
        _hotstuff = (XdcHotStuff)_blockchainTests.BlockProducerRunner;
    }

    [Test]
    public async Task Should_Update_MasterNodes_On_GapBlock()
    {
        // this test is basically an emulation because our block producer test setup does not support saving snapshots yet
        // add blocks until the next gap block
        var spec = _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header!);

        var oldHead = (XdcBlockHeader)_tree.Head!.Header!;
        var snapshotBefore = _blockchainTests.SnapshotManager.GetSnapshot(oldHead.Number, _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header!));

        Assert.That(snapshotBefore, Is.Not.Null);
        Assert.That(snapshotBefore.NextEpochCandidates.Length, Is.EqualTo(_masterNodesGenesisCount));

        var gapBlock = await _blockchainTests.AddBlock();
        while (!ISnapshotManager.IsTimeforSnapshot(_tree.Head!.Header!.Number, spec))
        {
            gapBlock = await _blockchainTests.AddBlock();
        }

        // simulate adding a new validator
        var newValidator = new PrivateKeyGenerator().Generate();
        _blockchainTests.MasterNodeCandidates.Add(newValidator);

        // mine the gap block that should trigger master node update

        var newHead = (XdcBlockHeader)_tree.Head!.Header!;
        Assert.That(newHead.Number, Is.EqualTo(gapBlock.Number));

        var snapshotAfter = _blockchainTests.SnapshotManager.GetSnapshot(newHead.Number, _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header!));

        Assert.That(snapshotAfter, Is.Not.Null);
        Assert.That(snapshotAfter.BlockNumber, Is.EqualTo(gapBlock.Number));
        Assert.That(snapshotAfter.NextEpochCandidates.Length, Is.EqualTo(_masterNodesGenesisCount + 1));
        Assert.That(snapshotAfter.NextEpochCandidates.Contains(newValidator.Address), Is.True);
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

    [Test]
    public async Task Update_Multiple_MasterNodes()
    {
        IXdcReleaseSpec? spec = _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header);

        var header = (XdcBlockHeader)_blockchainTests.BlockTree.Head!.Header!;
        spec = _blockchainTests.SpecProvider.GetXdcSpec(header);
        var snapshot = _blockchainTests.SnapshotManager.GetSnapshotByBlockNumber(header.Number, spec);

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.BlockNumber, Is.EqualTo(0));
        Assert.That(snapshot.NextEpochCandidates.Length, Is.EqualTo(_masterNodesGenesisCount));

        var gapBlock = await _blockchainTests.AddBlock();
        while (!ISnapshotManager.(_tree.Head!.Header!.Number, spec))
        {
            Console.WriteLine($"Adding block {_tree.Head!.Header!.Number + 1}");
            gapBlock  = await _blockchainTests.AddBlock();
        }

        header = (XdcBlockHeader)gapBlock.Header!;
        spec = _blockchainTests.SpecProvider.GetXdcSpec(header);
        snapshot = _blockchainTests.SnapshotManager.GetSnapshot(header.Number, spec);

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.BlockNumber, Is.EqualTo(gapBlock.Number));
        Assert.That(snapshot.NextEpochCandidates.Length, Is.EqualTo(_blockchainTests.MasterNodeCandidates.Count));

        long tstamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // --- Create header for block 1800
        var gapBlockChild = (XdcBlockHeader)(await _blockchainTests.AddBlockFromParent(_tree.Head!.Header!)).Header;

        // --- Validate header fields
        int validatorCount = gapBlockChild.Validator!.Length / Address.Size;
        int penaltyCount = (gapBlockChild.Penalties?.Length ?? 0) / Address.Size;

        Assert.That(validatorCount, Is.EqualTo(spec.MaxMasternodes));
        Assert.That(penaltyCount, Is.EqualTo(0));
    }
}
