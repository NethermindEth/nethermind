// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

internal class PenaltyTests
{
    [Test]
    public async Task TestHookPenaltyV2Comeback()
    {
        XdcTestBlockchain chain = await XdcTestBlockchain.Create(blocksToAdd: 0, withPenalty: true);
        ConfigureFastPenaltySpec(chain);

        PenaltyHandler penaltyHandler = CreatePenaltyHandler(chain);
        ISigningTxCache signingTxCache = chain.Container.Resolve<ISigningTxCache>();
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        await chain.AddBlocks(spec.EpochLength * 3);

        spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        long epoch = spec.EpochLength;
        long targetHeight = spec.SwitchBlock + epoch * 3;

        XdcBlockHeader firstEpochHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch + 1)!;
        EpochSwitchInfo? epochInfo = chain.EpochSwitchManager.GetEpochSwitchInfo(firstEpochHeader);
        Assert.That(epochInfo, Is.Not.Null);
        Address[] masternodes = epochInfo.Masternodes;

        XdcBlockHeader targetHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(targetHeight)!;
        Address[] penalties = penaltyHandler.HandlePenalties(targetHeader.Number, targetHeader.ParentHash!, masternodes);
        penalties.Length.Should().Be(1);
        PrivateKey penaltyHistorySigner = GetPenaltyHistorySigner(chain, penalties[0]);

        XdcBlockHeader signedHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(targetHeader.Number - spec.MergeSignRange)!;
        Transaction tx = BuildSigningTx(spec, signedHeader.Number, signedHeader.Hash!, penaltyHistorySigner);
        signingTxCache.SetSigningTransactions(signedHeader.Hash!, [tx]);

        penalties = penaltyHandler.HandlePenalties(targetHeader.Number, targetHeader.ParentHash!, masternodes);
        penalties.Length.Should().Be(0);
    }

    [Test]
    public async Task TestHookPenaltyV2Jump()
    {
        XdcTestBlockchain chain = await XdcTestBlockchain.Create(blocksToAdd: 0, withPenalty: true);
        ConfigureFastPenaltySpec(chain);

        PenaltyHandler penaltyHandler = CreatePenaltyHandler(chain);
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        await chain.AddBlocks(spec.EpochLength * 3);

        spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        long epoch = spec.EpochLength;
        long targetHeight = spec.SwitchBlock + epoch * 3 - spec.MergeSignRange;

        XdcBlockHeader firstEpochHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch + 1)!;
        Address[] masternodes = chain.EpochSwitchManager.GetEpochSwitchInfo(firstEpochHeader)!.Masternodes;

        XdcBlockHeader targetHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(targetHeight)!;

        Address[] penalties = penaltyHandler.HandlePenalties(targetHeader.Number, targetHeader.ParentHash!, masternodes);
        penalties.Length.Should().Be(1);
    }

    [Test]
    public async Task TestGetPenalties()
    {
        XdcTestBlockchain chain = await XdcTestBlockchain.Create(blocksToAdd: 0, withPenalty: true);
        ConfigureFastPenaltySpec(chain);
        PenaltyHandler penaltyHandler = CreatePenaltyHandler(chain);
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        await chain.AddBlocks(spec.EpochLength * 3);

        spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        long epoch = spec.EpochLength;

        XdcBlockHeader headerBeforeThirdEpochSwitch = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch * 3 - 1)!;
        XdcBlockHeader headerAfterSecondEpochSwitch = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch * 2 + 1)!;

        penaltyHandler.GetPenalties(headerBeforeThirdEpochSwitch).Length.Should().Be(1);
        penaltyHandler.GetPenalties(headerAfterSecondEpochSwitch).Length.Should().Be(1);
    }

    [Test]
    public async Task TestHookPenaltyParoleeCustomized()
    {
        // Epoch-switch penalties pattern by epoch index: true, true, true, false, true, true, true
        bool[] penaltyOrNot = [true, true, true, false, true, true, true];
        XdcTestBlockchain chain = await XdcTestBlockchain.Create(
            blocksToAdd: 0,
            withPenalty: true,
            penaltyOrNotByEpoch: penaltyOrNot);
        ConfigureFastPenaltySpec(chain, true);
        IXdcReleaseSpec initialSpec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        await chain.AddBlocks(initialSpec.EpochLength * 7);

        PenaltyHandler penaltyHandler = CreatePenaltyHandler(chain);
        ISigningTxCache signingTxCache = chain.Container.Resolve<ISigningTxCache>();
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec((XdcBlockHeader)chain.BlockTree.Head!.Header);
        long epoch = spec.EpochLength;

        XdcBlockHeader firstEpochHeader = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch + 1)!;
        EpochSwitchInfo firstEpochInfo = chain.EpochSwitchManager.GetEpochSwitchInfo(firstEpochHeader)!;
        Address[] masternodes = firstEpochInfo.Masternodes;

        XdcBlockHeader headerAtSeventhEpoch = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch * 7)!;
        Address[] penalties = penaltyHandler.HandlePenalties(headerAtSeventhEpoch.Number, headerAtSeventhEpoch.ParentHash!, masternodes);
        penalties.Length.Should().Be(1);
        PrivateKey penaltyHistorySigner = GetPenaltyHistorySigner(chain, penalties[0]);

        XdcBlockHeader headerAtSeventhEpochMinusMerge = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch * 7 - spec.MergeSignRange)!;
        signingTxCache.SetSigningTransactions(headerAtSeventhEpochMinusMerge.Hash!, [BuildSigningTx(spec, headerAtSeventhEpochMinusMerge.Number, headerAtSeventhEpochMinusMerge.Hash!, penaltyHistorySigner)]);

        XdcBlockHeader headerAtSeventhEpochMinusTwoMerges = (XdcBlockHeader)chain.BlockTree.FindHeader(spec.SwitchBlock + epoch * 7 - spec.MergeSignRange * 2)!;
        signingTxCache.SetSigningTransactions(headerAtSeventhEpochMinusTwoMerges.Hash!, [BuildSigningTx(spec, headerAtSeventhEpochMinusTwoMerges.Number, headerAtSeventhEpochMinusTwoMerges.Hash!, penaltyHistorySigner, 1)]);

        penalties = penaltyHandler.HandlePenalties(headerAtSeventhEpoch.Number, headerAtSeventhEpoch.ParentHash!, masternodes);
        penalties.Length.Should().Be(1);
    }

    private static PenaltyHandler CreatePenaltyHandler(XdcTestBlockchain chain)
    {
        IEthereumEcdsa ecdsa = chain.Container.Resolve<IEthereumEcdsa>();
        ISigningTxCache signingTxCache = chain.Container.Resolve<ISigningTxCache>();
        return new PenaltyHandler(chain.BlockTree, ecdsa, chain.SpecProvider, chain.EpochSwitchManager, signingTxCache);
    }

    private static void ConfigureFastPenaltySpec(XdcTestBlockchain chain, bool activatePenaltyUpgrade = false)
    {
        chain.ChangeReleaseSpec(spec =>
        {
            spec.EpochLength = 90;
            spec.Gap = 45;
            spec.TipUpgradePenalty = activatePenaltyUpgrade ? 0 : long.MaxValue;
            spec.RangeReturnSigner = 150;
        });
    }

    private static PrivateKey GetPenaltyHistorySigner(XdcTestBlockchain chain, Address penaltyAddress)
    {
        return chain.MasterNodeCandidates.First(k => k.Address == penaltyAddress);
    }

    private static Transaction BuildSigningTx(IXdcReleaseSpec spec, long blockNumber, Hash256 blockHash, PrivateKey signer, long nonce = 0)
    {
        return Build.A.Transaction
            .WithChainId(0)
            .WithNonce((UInt256)nonce)
            .WithGasLimit(200000)
            .WithXdcSigningData(blockNumber, blockHash)
            .ToBlockSignerContract(spec)
            .SignedAndResolved(signer)
            .TestObject;
    }
}
