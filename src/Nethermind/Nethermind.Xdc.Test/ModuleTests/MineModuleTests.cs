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
    private const int _masterNodesGenesisCount = 100;

    public async Task<(XdcTestBlockchain, XdcBlockTree)> Setup()
    {
        var _blockchainTests = await XdcTestBlockchain.Create(useHotStuffModule: true, masterNodesCount: _masterNodesGenesisCount);
        var _tree = (XdcBlockTree)_blockchainTests.BlockTree;

        return (_blockchainTests, _tree);
    }

    [Test]
    public async Task TestUpdateMultipleMasterNodes()
    {
        var (_blockchainTests, _tree) = await Setup();

        // this test is basically an emulation because our block producer test setup does not support saving snapshots yet
        // add blocks until the next gap block
        var spec = _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header!);

        var oldHead = (XdcBlockHeader)_tree.Head!.Header!;
        var snapshotBefore = _blockchainTests.SnapshotManager.GetSnapshotByBlockNumber(oldHead.Number, _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header!));

        Assert.That(snapshotBefore, Is.Not.Null);
        Assert.That(snapshotBefore.NextEpochCandidates.Length, Is.EqualTo(_masterNodesGenesisCount));

        // simulate adding a new validator
        var newValidator = new PrivateKeyGenerator().Generate();
        _blockchainTests.MasterNodeCandidates.Add(newValidator);

        // mine the gap block that should trigger master node update
        var gapBlock = await _blockchainTests.AddBlock();
        while (!ISnapshotManager.IsTimeforSnapshot(_tree.Head!.Header!.Number, spec))
        {
            gapBlock = await _blockchainTests.AddBlock();
        }

        var newHead = (XdcBlockHeader)_tree.Head!.Header!;
        Assert.That(newHead.Number, Is.EqualTo(gapBlock.Number));

        var snapshotAfter = _blockchainTests.SnapshotManager.GetSnapshotByGapNumber((ulong)newHead.Number);

        Assert.That(snapshotAfter, Is.Not.Null);
        Assert.That(snapshotAfter.BlockNumber, Is.EqualTo(gapBlock.Number));
        Assert.That(snapshotAfter.NextEpochCandidates.Length, Is.EqualTo(_blockchainTests.MasterNodeCandidates.Count));
        Assert.That(snapshotAfter.NextEpochCandidates.Contains(newValidator.Address), Is.True);
    }

    [Test]
    public async Task TestShouldMineOncePerRound()
    {
        var (_blockchainTests, _tree) = await Setup();

        var _hotstuff = (XdcHotStuff)_blockchainTests.BlockProducerRunner;

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
    public async Task TestUpdateMasterNodes()
    {
        var (_blockchainTests, _tree) = await Setup();

        _blockchainTests.ChangeReleaseSpec((spec) =>
        {
            spec.EpochLength = 90;
            spec.Gap = 45;
        });

        IXdcReleaseSpec? spec = _blockchainTests.SpecProvider.GetXdcSpec((XdcBlockHeader)_tree.Head!.Header);

        var header = (XdcBlockHeader)_blockchainTests.BlockTree.Head!.Header!;
        spec = _blockchainTests.SpecProvider.GetXdcSpec(header);
        var snapshot = _blockchainTests.SnapshotManager.GetSnapshotByBlockNumber(header.Number, spec);

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.BlockNumber, Is.EqualTo(0));
        Assert.That(snapshot.NextEpochCandidates.Length, Is.EqualTo(_masterNodesGenesisCount));

        var gapBlock = await _blockchainTests.AddBlock();
        while (!ISnapshotManager.IsTimeforSnapshot(_tree.Head!.Header!.Number, spec))
        {
            gapBlock  = await _blockchainTests.AddBlock();
        }

        Assert.That(gapBlock.Number, Is.EqualTo(spec.Gap));

        header = (XdcBlockHeader)gapBlock.Header!;
        spec = _blockchainTests.SpecProvider.GetXdcSpec(header);
        snapshot = _blockchainTests.SnapshotManager.GetSnapshotByGapNumber((ulong)header.Number);

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.BlockNumber, Is.EqualTo(gapBlock.Number));
        Assert.That(snapshot.NextEpochCandidates.Length, Is.EqualTo(_blockchainTests.MasterNodeCandidates.Count));

        long tstamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var epochSwitchBlock = await _blockchainTests.AddBlock();
        while (!_blockchainTests.EpochSwitchManager.IsEpochSwitchAtBlock((XdcBlockHeader)_tree.Head!.Header!))
        {
            epochSwitchBlock = await _blockchainTests.AddBlock();
        }

        Assert.That(epochSwitchBlock.Number, Is.EqualTo(spec.EpochLength));

        header = (XdcBlockHeader)epochSwitchBlock.Header!;
        // --- Validate header fields
        int validatorCount = header.Validators!.Length / Address.Size;
        int penaltyCount = header.Penalties!.Length / Address.Size;

        Assert.That(validatorCount, Is.EqualTo(spec.MaxMasternodes));
    }
}
