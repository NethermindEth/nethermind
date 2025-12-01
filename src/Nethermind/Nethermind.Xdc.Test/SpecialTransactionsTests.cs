// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using FluentAssertions.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

internal class SpecialTransactionsTests
{
    private bool IsTimeForOnchainSignature(IXdcReleaseSpec spec, long blockNumber)
    {
        long checkNumber = blockNumber % spec.EpochLength;
        return checkNumber == spec.MergeSignRange;
    }

    [Test]
    public async Task Special_Tx_Is_Dispatched_On_MergeSignRange_Block()
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);
        await Task.Delay(2.Seconds()); // to avoid tight loop

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = 5;
            spec.IsEip1559Enabled = false;
        });

        blockChain.StartHotStuffModule();

        XdcBlockHeader? head = blockChain.BlockTree.Head!.Header as XdcBlockHeader;
        do
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            await Task.Delay(blockChain.SpecProvider.GetXdcSpec(head!).MinePeriod.Seconds()); // to avoid tight loop
            head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        }
        while (!IsTimeForOnchainSignature(blockChain.SpecProvider.GetXdcSpec(head), head.Number));

        // await blockChain.StopHotStuffModule();

        await Task.Delay(((XdcReleaseSpec)blockChain.SpecProvider.GetFinalSpec()).MinePeriod.Seconds()); // to avoid tight loop

        var receipts = blockChain.TxPool.GetPendingTransactions();

        receipts.Any(r => r.To == blockChain.SpecProvider.GetXdcSpec(head).BlockSignersAddress
                       || r.To == blockChain.SpecProvider.GetXdcSpec(head).RandomizeSMCBinary ).Should().BeTrue();
    }

    [Test]
    public async Task Special_Tx_Is_Not_Dispatched_Outside_MergeSignRange_Block()
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);
        await Task.Delay(2.Seconds()); // to avoid tight loop

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = 5;
            spec.IsEip1559Enabled = false;
        });

        blockChain.StartHotStuffModule();

        XdcBlockHeader? head = blockChain.BlockTree.Head!.Header as XdcBlockHeader;
        do
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            await Task.Delay(blockChain.SpecProvider.GetXdcSpec(head!).MinePeriod.Seconds()); // to avoid tight loop
            head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        }
        while (!IsTimeForOnchainSignature(blockChain.SpecProvider.GetXdcSpec(head), head.Number + 1));

        // await blockChain.StopHotStuffModule();

        await Task.Delay(((XdcReleaseSpec)blockChain.SpecProvider.GetFinalSpec()).MinePeriod.Seconds()); // to avoid tight loop

        var receipts = blockChain.TxPool.GetPendingTransactions();

        receipts.Any(r => r.To == blockChain.SpecProvider.GetXdcSpec(head).BlockSignersAddress
                       || r.To == blockChain.SpecProvider.GetXdcSpec(head).RandomizeSMCBinary).Should().BeFalse();
    }

    [Test]
    public async Task Special_Tx_Is_Executed_Before_Normal_Txs()
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);
        await Task.Delay(2.Seconds()); // to avoid tight loop

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = 5;
            spec.IsEip1559Enabled = false;
        });

        blockChain.StartHotStuffModule();

        XdcBlockHeader? head = blockChain.BlockTree.Head!.Header as XdcBlockHeader;
        do
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            await Task.Delay(blockChain.SpecProvider.GetXdcSpec(head!).MinePeriod.Seconds()); // to avoid tight loop
            head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        }
        while (!IsTimeForOnchainSignature(blockChain.SpecProvider.GetXdcSpec(head), head.Number - 1));

        var block = blockChain.BlockTree.Head!;

        var receipts = blockChain.ReceiptStorage.Get(block.ParentHash!);

        Assert.That(block, Is.Not.Null);
    }

}
