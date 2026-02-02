// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

internal class PenaltyTests
{
    // [Test]
    // public async Task TestHookPenaltyV2Mining()
    // {
    //     var chain = await XdcTestBlockchain.Create();
    //     var penaltyHandler = XdcPenaltyTestUtils.CreatePenaltyHandler(chain);
    //
    //     // Advance to 3 epochs
    //     IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(chain.BlockTree.Head!.Header);
    //     await chain.AddBlocks(spec.EpochLength * 3);
    //
    //     var header901 = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.EpochLength + 1)!;
    //     var masternodes = XdcPenaltyTestUtils.GetMasternodes(chain, header901);
    //     masternodes.Length.Should().Be(5);
    //
    //     var header2100 = (XdcBlockHeader)chain.BlockTree.Head!.Header;
    //
    //     Address[] penalties = penaltyHandler.HandlePenalties(
    //         (long)header2100.Number,
    //         header2100.ParentHash!,
    //         masternodes);
    //
    //     penalties.Length.Should().Be(1);
    // }
    //
    // [Test]
    // public async Task TestHookPenaltyV2Comeback()
    // {
    //     var chain = await XdcTestBlockchain.Create(withPenalty: true);
    //     var penaltyHandler = XdcPenaltyTestUtils.CreatePenaltyHandler(chain);
    //
    //     IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(chain.BlockTree.Head!.Header);
    //
    //     await chain.AddBlocks(spec.EpochLength * 3);
    //
    //     var header901 = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.EpochLength + 1)!;
    //     var masternodes = XdcPenaltyTestUtils.GetMasternodes(chain, header901);
    //
    //     var header2100 = (XdcBlockHeader)chain.BlockTree.Head!.Header;
    //
    //     // First check: comeback still active
    //     var penalties = penaltyHandler.HandlePenalties(
    //         (long)header2100.Number,
    //         header2100.ParentHash!,
    //         masternodes);
    //
    //     penalties.Length.Should().Be(2);
    //
    //     // Insert signing tx to cancel comeback
    //     var header2085 = (XdcBlockHeader)chain.BlockTree.FindHeader(
    //         header2100.Number - spec.MergeSignRange)!;
    //
    //     var signer = chain.TakeRandomMasterNode(spec, header2085);
    //     var tx = XdcTestBlockchain.BuildSigningTx(
    //         spec,
    //         header2085.Number,
    //         header2085.Hash!,
    //         signer);
    //
    //     await chain.AddBlock(tx);
    //
    //     penalties = penaltyHandler.HandlePenalties(
    //         (long)header2100.Number,
    //         header2100.ParentHash!,
    //         masternodes);
    //
    //     penalties.Length.Should().Be(1);
    // }
    //
    // [Test]
    // public async Task TestHookPenaltyV2Jump()
    // {
    //     var chain = await XdcTestBlockchain.Create(withPenalty: true);
    //     var penaltyHandler = XdcPenaltyTestUtils.CreatePenaltyHandler(chain);
    //
    //     IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(chain.BlockTree.Head!.Header);
    //
    //     await chain.AddBlocks(spec.EpochLength * 3 - spec.MergeSignRange);
    //
    //     var header = (XdcBlockHeader)chain.BlockTree.Head!.Header;
    //     var masternodes = XdcPenaltyTestUtils.GetMasternodes(chain, header);
    //
    //     var penalties = penaltyHandler.HandlePenalties(
    //         (long)header.Number,
    //         header.ParentHash!,
    //         masternodes);
    //
    //     penalties.Length.Should().Be(2);
    // }
    //
    // [Test]
    // public async Task TestHookPenaltyV2Mining()
    // {
    //     var chain = await XdcTestBlockchain.Create();
    //     var penaltyHandler = XdcPenaltyTestUtils.CreatePenaltyHandler(chain);
    //
    //     // Advance to 3 epochs
    //     IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(chain.BlockTree.Head!.Header);
    //     await chain.AddBlocks(spec.EpochLength * 3);
    //
    //     var header901 = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.EpochLength + 1)!;
    //     var masternodes = XdcPenaltyTestUtils.GetMasternodes(chain, header901);
    //     masternodes.Length.Should().Be(5);
    //
    //     var header2100 = (XdcBlockHeader)chain.BlockTree.Head!.Header;
    //
    //     Address[] penalties = penaltyHandler.HandlePenalties(
    //         (long)header2100.Number,
    //         header2100.ParentHash!,
    //         masternodes);
    //
    //     penalties.Length.Should().Be(1);
    // }

    [Test]
    public async Task TestGetPenalties()
    {
        var chain = await XdcTestBlockchain.Create(withPenalty: true);
        var penaltyHandler = XdcPenaltyTestUtils.CreatePenaltyHandler(chain);

        await chain.AddBlocks(2700);

        var header2699 = (XdcBlockHeader)chain.BlockTree.FindHeader(2699)!;
        var header1801 = (XdcBlockHeader)chain.BlockTree.FindHeader(1801)!;

        penaltyHandler.GetPenalties(header2699).Length.Should().Be(1);
        penaltyHandler.GetPenalties(header1801).Length.Should().Be(1);
    }

    // [Test]
    // public async Task TestHookPenaltyParolee()
    // {
    //     var chain = await XdcTestBlockchain.Create(withPenalty: true, tipUpgradeAt: 0);
    //     var penaltyHandler = XdcPenaltyTestUtils.CreatePenaltyHandler(chain);
    //
    //     IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(chain.BlockTree.Head!.Header);
    //
    //     await chain.AddBlocks(spec.EpochLength * 4);
    //
    //     var header3600 = (XdcBlockHeader)chain.BlockTree.Head!.Header;
    //     var masternodes = XdcPenaltyTestUtils.GetMasternodes(chain, header3600);
    //
    //     var penalties = penaltyHandler.HandlePenalties(
    //         (long)header3600.Number,
    //         header3600.ParentHash!,
    //         masternodes);
    //
    //     penalties.Length.Should().Be(2);
    //
    //     // Insert signing txs to reduce penalty
    //     var header3570 = (XdcBlockHeader)chain.BlockTree.FindHeader(
    //         header3600.Number - spec.MergeSignRange * 2)!;
    //
    //     var signer = chain.TakeRandomMasterNode(spec, header3570);
    //     var tx = XdcTestBlockchain.BuildSigningTx(
    //         spec,
    //         header3570.Number,
    //         header3570.Hash!,
    //         signer);
    //
    //     await chain.AddBlock(tx);
    //
    //     penalties = penaltyHandler.HandlePenalties(
    //         (long)header3600.Number,
    //         header3600.ParentHash!,
    //         masternodes);
    //
    //     penalties.Length.Should().Be(1);
    // }
}
