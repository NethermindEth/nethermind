// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

public class ForensicsTests
{
    [Test]
    public async Task TestProcessQcShallSetForensicsCommittedQc()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();
        IXdcConsensusContext context = blockchain.XdcContext;

        Block freshBlock = await blockchain.AddBlockWithoutCommitQc();
        XdcBlockHeader targetHeader = (XdcBlockHeader)freshBlock.Header;

        IXdcReleaseSpec releaseSpec = blockchain.SpecProvider.GetXdcSpec(targetHeader, context.CurrentRound);
        EpochSwitchInfo switchInfo = blockchain.EpochSwitchManager.GetEpochSwitchInfo(targetHeader)!;
        PrivateKey[] keys = blockchain.TakeRandomMasterNodes(releaseSpec, switchInfo);

        BlockRoundInfo blockInfo = new(freshBlock.Hash!, context.CurrentRound, freshBlock.Number);
        ulong gap = (ulong)Math.Max(0, switchInfo.EpochSwitchBlockInfo.BlockNumber - switchInfo.EpochSwitchBlockInfo.BlockNumber % releaseSpec.EpochLength - releaseSpec.Gap);

        for (int i = 0; i < keys.Length - 1; i++)
        {
            Vote vote = XdcTestHelper.BuildSignedVote(blockInfo, gap, keys[i]);
            await blockchain.VotesManager.HandleVote(vote);
        }

        PrivateKey randomKey = blockchain.RandomKeys.First();
        Vote randomVote = XdcTestHelper.BuildSignedVote(blockInfo, gap, randomKey);
        await blockchain.VotesManager.HandleVote(randomVote);

        // Final valid vote reaches QC threshold and should trigger forensics state update.
        Vote lastVote = XdcTestHelper.BuildSignedVote(blockInfo, gap, keys.Last());
        await blockchain.VotesManager.HandleVote(lastVote);

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (forensicsProcessor.GetHighestCommittedQcsSnapshot().Length != 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        QuorumCertificate[] highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));

        XdcBlockHeader? parentHeader = (XdcBlockHeader?)blockchain.BlockTree.FindHeader(targetHeader.ParentHash!);
        Assert.That(parentHeader, Is.Not.Null);
        Assert.That(parentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(targetHeader.ExtraConsensusData, Is.Not.Null);

        Assert.That(highestCommittedQcs[0], Is.EqualTo(parentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[1], Is.EqualTo(targetHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[2], Is.EqualTo(context.HighestQC));
    }

    [Test]
    public async Task TestSetCommittedQCsInOrder()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 5, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader currentHeader = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader parentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(currentHeader.ParentHash!)!;
        XdcBlockHeader grandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(parentHeader.ParentHash!)!;
        XdcBlockHeader greatGrandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(grandParentHeader.ParentHash!)!;
        XdcBlockHeader greatGreatGrandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(greatGrandParentHeader.ParentHash!)!;

        Assert.That(currentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(parentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(grandParentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(greatGrandParentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(greatGreatGrandParentHeader.ExtraConsensusData, Is.Not.Null);

        QuorumCertificate[] beforeInvalidOrder = forensicsProcessor.GetHighestCommittedQcsSnapshot();

        await forensicsProcessor.SetCommittedQCs(
            [grandParentHeader, greatGrandParentHeader],
            currentHeader.ExtraConsensusData!.QuorumCert);

        QuorumCertificate[] highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs, Is.EqualTo(beforeInvalidOrder));

        await forensicsProcessor.SetCommittedQCs(
            [grandParentHeader, parentHeader],
            currentHeader.ExtraConsensusData!.QuorumCert);

        highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
        Assert.That(highestCommittedQcs[0], Is.EqualTo(grandParentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[1], Is.EqualTo(parentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[2], Is.EqualTo(currentHeader.ExtraConsensusData!.QuorumCert));

        await forensicsProcessor.SetCommittedQCs(
            [greatGrandParentHeader, grandParentHeader],
            parentHeader.ExtraConsensusData!.QuorumCert);

        highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
        Assert.That(highestCommittedQcs[0], Is.EqualTo(greatGrandParentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[1], Is.EqualTo(grandParentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[2], Is.EqualTo(parentHeader.ExtraConsensusData!.QuorumCert));
    }

    [Test]
    public async Task TestForensicsMonitoring()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 15, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader currentHeader = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader parentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(currentHeader.ParentHash!)!;
        XdcBlockHeader grandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(parentHeader.ParentHash!)!;

        XdcBlockHeader oldIncomingHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(grandParentHeader.ParentHash!)!;
        XdcBlockHeader oldParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(oldIncomingHeader.ParentHash!)!;
        XdcBlockHeader oldGrandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(oldParentHeader.ParentHash!)!;

        Assert.That(currentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(parentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(grandParentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(oldIncomingHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(oldParentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(oldGrandParentHeader.ExtraConsensusData, Is.Not.Null);

        // Seed forensics baseline with an older valid committed-QC triplet.
        await forensicsProcessor.SetCommittedQCs(
            [oldGrandParentHeader, oldParentHeader],
            oldIncomingHeader.ExtraConsensusData!.QuorumCert);

        QuorumCertificate[] previousCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(previousCommittedQcs.Length, Is.EqualTo(3));

        // Feed a newer same-chain window; happy-path behavior should update committed QCs without errors.
        Assert.That(async () => await forensicsProcessor.ForensicsMonitoring(
            [grandParentHeader, parentHeader],
            currentHeader.ExtraConsensusData!.QuorumCert), Throws.Nothing);

        QuorumCertificate[] highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
        Assert.That(highestCommittedQcs[0], Is.EqualTo(grandParentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[1], Is.EqualTo(parentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[2], Is.EqualTo(currentHeader.ExtraConsensusData!.QuorumCert));
    }

    [Test]
    public async Task TestForensicsMonitoringNotOnSameChainButHaveSameRoundQC()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 15, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader currentHeader = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader parentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(currentHeader.ParentHash!)!;
        XdcBlockHeader grandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(parentHeader.ParentHash!)!;

        Assert.That(currentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(parentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(grandParentHeader.ExtraConsensusData, Is.Not.Null);

        // Seed highest committed QCs from an older canonical window.
        XdcBlockHeader oldIncomingHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(grandParentHeader.ParentHash!)!;
        XdcBlockHeader oldParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(oldIncomingHeader.ParentHash!)!;
        XdcBlockHeader oldGrandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(oldParentHeader.ParentHash!)!;

        // Force a disjoint baseline round space while preserving required QC hash links.
        QuorumCertificate oldGrandParentQc = new(
            new BlockRoundInfo(
                oldGrandParentHeader.ExtraConsensusData!.QuorumCert.ProposedBlockInfo.Hash,
                1_000,
                oldGrandParentHeader.ExtraConsensusData.QuorumCert.ProposedBlockInfo.BlockNumber),
            oldGrandParentHeader.ExtraConsensusData.QuorumCert.Signatures,
            oldGrandParentHeader.ExtraConsensusData.QuorumCert.GapNumber);
        oldGrandParentHeader.ExtraConsensusData = new ExtraFieldsV2(oldGrandParentHeader.ExtraConsensusData.BlockRound, oldGrandParentQc);

        QuorumCertificate oldParentQc = new(
            new BlockRoundInfo(
                oldGrandParentHeader.Hash!,
                1_001,
                oldGrandParentHeader.Number),
            oldParentHeader.ExtraConsensusData!.QuorumCert.Signatures,
            oldParentHeader.ExtraConsensusData.QuorumCert.GapNumber);
        oldParentHeader.ExtraConsensusData = new ExtraFieldsV2(oldParentHeader.ExtraConsensusData.BlockRound, oldParentQc);

        QuorumCertificate oldIncomingQc = new(
            new BlockRoundInfo(
                oldParentHeader.Hash!,
                1_002,
                oldParentHeader.Number),
            oldIncomingHeader.ExtraConsensusData!.QuorumCert.Signatures,
            oldIncomingHeader.ExtraConsensusData.QuorumCert.GapNumber);

        await forensicsProcessor.SetCommittedQCs(
            [oldGrandParentHeader, oldParentHeader],
            oldIncomingQc);

        // Build a fork from the same grandparent; this gives a divergent chain segment.
        Block forkBlock1 = await blockchain.AddBlockFromParent(grandParentHeader);
        XdcBlockHeader forkHeader1 = (XdcBlockHeader)forkBlock1.Header;
        Block forkBlock2 = await blockchain.AddBlockFromParent(forkHeader1);
        XdcBlockHeader forkHeader2 = (XdcBlockHeader)forkBlock2.Header;

        Assert.That(forkHeader1.ExtraConsensusData, Is.Not.Null);
        Assert.That(forkHeader2.ExtraConsensusData, Is.Not.Null);
        Assert.That(forkHeader1.Hash, Is.Not.EqualTo(parentHeader.Hash));

        // Normalize the fork window so SetCommittedQCs validation passes:
        // qc on forkHeader1 must point to grandParentHeader.
        QuorumCertificate forkHeader1Qc = new(
            new BlockRoundInfo(
                grandParentHeader.Hash!,
                parentHeader.ExtraConsensusData!.QuorumCert.ProposedBlockInfo.Round,
                grandParentHeader.Number),
            [],
            forkHeader1.ExtraConsensusData!.QuorumCert.GapNumber);
        forkHeader1.ExtraConsensusData = new ExtraFieldsV2(forkHeader1.ExtraConsensusData.BlockRound, forkHeader1Qc);

        // Compose incoming fork QCs in ancestor->descendant order and verify the same-round condition.
        QuorumCertificate[] incomingForkQcs = [
            grandParentHeader.ExtraConsensusData!.QuorumCert,
            forkHeader1Qc,
            forkHeader2.ExtraConsensusData!.QuorumCert
        ];
        // Build deterministic incoming QC that points to forkHeader1, matching SetCommittedQCs expectations.
        QuorumCertificate incomingForkMonitoringQc = new(
            new BlockRoundInfo(
                forkHeader1.Hash!,
                forkHeader1.ExtraConsensusData!.BlockRound,
                forkHeader1.Number),
            [],
            forkHeader2.ExtraConsensusData!.QuorumCert.GapNumber);
        QuorumCertificate[] highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        bool hasSameRoundQc = forensicsProcessor.TryFindQCsInSameRound(
            highestCommittedQcs,
            incomingForkQcs,
            out _,
            out _);
        Assert.That(hasSameRoundQc, Is.True);

        ForensicsEvent forensicsEvent = await CaptureForensicsEvent(
            forensicsProcessor,
            () => forensicsProcessor.ForensicsMonitoring([grandParentHeader, forkHeader1], incomingForkMonitoringQc));
        Assert.That(forensicsEvent.ForensicsProof.ForensicsType, Is.EqualTo("QC"));
        using JsonDocument content = JsonDocument.Parse(forensicsEvent.ForensicsProof.Content);
        ulong smallerRound = ReadUInt64(content.RootElement
            .GetProperty("smallerRoundInfo")
            .GetProperty("quorumCert")
            .GetProperty("proposedBlockInfo")
            .GetProperty("round"));
        ulong largerRound = ReadUInt64(content.RootElement
            .GetProperty("largerRoundInfo")
            .GetProperty("quorumCert")
            .GetProperty("proposedBlockInfo")
            .GetProperty("round"));
        bool acrossEpoch = content.RootElement.GetProperty("acrossEpoch").GetBoolean();

        Assert.That(smallerRound, Is.EqualTo(largerRound));
        Assert.That(acrossEpoch, Is.False);

        // Forensics monitoring always refreshes committed-QC snapshot with the incoming valid window.
        highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
        Assert.That(highestCommittedQcs[0], Is.SameAs(grandParentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[1], Is.SameAs(forkHeader1Qc));
        Assert.That(highestCommittedQcs[2], Is.SameAs(incomingForkMonitoringQc));
    }

    [Test]
    public async Task TestForensicsMonitoringNotOnSameChainDoNotHaveSameRoundQC()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 15, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader currentHeader = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader parentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(currentHeader.ParentHash!)!;
        XdcBlockHeader grandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(parentHeader.ParentHash!)!;

        Assert.That(currentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(parentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(grandParentHeader.ExtraConsensusData, Is.Not.Null);

        // Seed highest committed QCs from the canonical chain tip window.
        await forensicsProcessor.SetCommittedQCs(
            [grandParentHeader, parentHeader],
            currentHeader.ExtraConsensusData!.QuorumCert);

        // Build a fork from the same grandparent; this gives a divergent chain segment.
        Block forkBlock1 = await blockchain.AddBlockFromParent(grandParentHeader);
        XdcBlockHeader forkHeader1 = (XdcBlockHeader)forkBlock1.Header;
        Block forkBlock2 = await blockchain.AddBlockFromParent(forkHeader1);
        XdcBlockHeader forkHeader2 = (XdcBlockHeader)forkBlock2.Header;

        Assert.That(forkHeader1.ExtraConsensusData, Is.Not.Null);
        Assert.That(forkHeader2.ExtraConsensusData, Is.Not.Null);
        Assert.That(forkHeader1.Hash, Is.Not.EqualTo(parentHeader.Hash));

        // Construct fork QCs with rounds intentionally far from canonical rounds.
        ulong forkRound1 = currentHeader.ExtraConsensusData.BlockRound + 20;
        ulong forkRound2 = forkRound1 + 1;

        QuorumCertificate forkHeader1Qc = new(
            new BlockRoundInfo(
                grandParentHeader.Hash!,
                forkRound1,
                grandParentHeader.Number),
            [],
            forkHeader1.ExtraConsensusData!.QuorumCert.GapNumber);
        forkHeader1.ExtraConsensusData = new ExtraFieldsV2(forkHeader1.ExtraConsensusData.BlockRound, forkHeader1Qc);

        QuorumCertificate incomingForkMonitoringQc = new(
            new BlockRoundInfo(
                forkHeader1.Hash!,
                forkRound2,
                forkHeader1.Number),
            [],
            forkHeader2.ExtraConsensusData!.QuorumCert.GapNumber);

        QuorumCertificate[] incomingForkQcs = [
            incomingForkMonitoringQc,
            forkHeader1Qc,
            grandParentHeader.ExtraConsensusData!.QuorumCert
        ];
        QuorumCertificate[] highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        bool hasSameRoundQc = forensicsProcessor.TryFindQCsInSameRound(
            highestCommittedQcs,
            incomingForkQcs,
            out _,
            out _);
        Assert.That(hasSameRoundQc, Is.False);

        ForensicsEvent forensicsEvent = await CaptureForensicsEvent(
            forensicsProcessor,
            () => forensicsProcessor.ForensicsMonitoring([grandParentHeader, forkHeader1], incomingForkMonitoringQc));
        Assert.That(forensicsEvent.ForensicsProof.ForensicsType, Is.EqualTo("QC"));
        using JsonDocument content = JsonDocument.Parse(forensicsEvent.ForensicsProof.Content);
        ulong smallerRound = ReadUInt64(content.RootElement
            .GetProperty("smallerRoundInfo")
            .GetProperty("quorumCert")
            .GetProperty("proposedBlockInfo")
            .GetProperty("round"));
        ulong largerRound = ReadUInt64(content.RootElement
            .GetProperty("largerRoundInfo")
            .GetProperty("quorumCert")
            .GetProperty("proposedBlockInfo")
            .GetProperty("round"));
        bool acrossEpoch = content.RootElement.GetProperty("acrossEpoch").GetBoolean();

        Assert.That(smallerRound, Is.LessThan(largerRound));
        Assert.That(acrossEpoch, Is.False);

        // Even in the no-same-round case, committed-QC snapshot should refresh to incoming valid window.
        highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
        Assert.That(highestCommittedQcs[0], Is.SameAs(grandParentHeader.ExtraConsensusData!.QuorumCert));
        Assert.That(highestCommittedQcs[1], Is.SameAs(forkHeader1Qc));
        Assert.That(highestCommittedQcs[2], Is.SameAs(incomingForkMonitoringQc));
    }

    [Test]
    public async Task TestVoteEquivocationSameRound()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 10, configurer: RegisterRealForensicsProcessor);
        IForensicsProcessor forensicsProcessor = blockchain.Container.Resolve<IForensicsProcessor>();
        IXdcConsensusContext context = blockchain.XdcContext;

        XdcBlockHeader currentHeader = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader parentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(currentHeader.ParentHash!)!;

        Assert.That(currentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(parentHeader.ExtraConsensusData, Is.Not.Null);

        // Build a sibling fork block at the same height as currentHeader.
        Block forkBlock = await blockchain.AddBlockFromParent(parentHeader);
        XdcBlockHeader forkHeader = (XdcBlockHeader)forkBlock.Header;
        Assert.That(forkHeader.Hash, Is.Not.EqualTo(currentHeader.Hash));

        // Same signer, same round, different block hashes => vote equivocation scenario.
        PrivateKey signerKey = blockchain.MasterNodeCandidates.First();
        ulong round = context.CurrentRound;
        ulong gap = currentHeader.ExtraConsensusData.QuorumCert.GapNumber;
        BlockRoundInfo canonicalRoundInfo = new(currentHeader.Hash!, round, currentHeader.Number);
        BlockRoundInfo forkRoundInfo = new(forkHeader.Hash!, round, forkHeader.Number);
        Vote canonicalVote = XdcTestHelper.BuildSignedVote(canonicalRoundInfo, gap, signerKey);
        Vote forkVote = XdcTestHelper.BuildSignedVote(forkRoundInfo, gap, signerKey);

        Assert.That(async () => await blockchain.VotesManager.HandleVote(canonicalVote), Throws.Nothing);
        ForensicsEvent forensicsEvent = await CaptureForensicsEvent(
            forensicsProcessor,
            () => blockchain.VotesManager.HandleVote(forkVote));
        Assert.That(forensicsEvent.ForensicsProof.ForensicsType, Is.EqualTo("Vote"));
        using JsonDocument content = JsonDocument.Parse(forensicsEvent.ForensicsProof.Content);
        ulong smallerRound = ReadUInt64(content.RootElement
            .GetProperty("smallerRoundVote")
            .GetProperty("proposedBlockInfo")
            .GetProperty("round"));
        ulong largerRound = ReadUInt64(content.RootElement
            .GetProperty("largerRoundVote")
            .GetProperty("proposedBlockInfo")
            .GetProperty("round"));
        string signer = content.RootElement.GetProperty("signer").GetString()!;

        Assert.That(smallerRound, Is.EqualTo(round));
        Assert.That(largerRound, Is.EqualTo(round));
        Assert.That(signer, Is.EqualTo(signerKey.Address.ToString()).IgnoreCase);
    }

    [Test]
    public async Task TestVoteEquivocationDifferentRound()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 15, configurer: RegisterRealForensicsProcessor);
        IForensicsProcessor forensicsProcessor = blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader currentHeader = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader parentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(currentHeader.ParentHash!)!;
        XdcBlockHeader grandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(parentHeader.ParentHash!)!;
        XdcBlockHeader greatGrandParentHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(grandParentHeader.ParentHash!)!;

        Assert.That(currentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(parentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(grandParentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(greatGrandParentHeader.ExtraConsensusData, Is.Not.Null);

        // Mirror XDC test intent: set committed QCs on canonical chain, then submit
        // a vote that does not extend from the committed ancestor and has a higher round.
        PrivateKey signerKey = blockchain.MasterNodeCandidates.First();
        ulong baselineQcRound = 14;
        ulong incomingVoteRound = 16;
        ulong gap = currentHeader.ExtraConsensusData!.QuorumCert.GapNumber;

        Vote qcSignedVote = XdcTestHelper.BuildSignedVote(
            new BlockRoundInfo(parentHeader.Hash!, baselineQcRound, parentHeader.Number),
            gap,
            signerKey);
        QuorumCertificate baselineIncomingQc = new(
            qcSignedVote.ProposedBlockInfo,
            [qcSignedVote.Signature!],
            gap);

        await forensicsProcessor.SetCommittedQCs(
            [grandParentHeader, parentHeader],
            baselineIncomingQc);

        Vote incomingVote = XdcTestHelper.BuildSignedVote(
            new BlockRoundInfo(greatGrandParentHeader.Hash!, incomingVoteRound, greatGrandParentHeader.Number),
            gap,
            signerKey);

        ForensicsEvent forensicsEvent = await CaptureForensicsEvent(
            forensicsProcessor,
            () => forensicsProcessor.ProcessVoteEquivocation(incomingVote));

        Assert.That(forensicsEvent.ForensicsProof.ForensicsType, Is.EqualTo("Vote"));
        using JsonDocument content = JsonDocument.Parse(forensicsEvent.ForensicsProof.Content);
        ulong smallerRound = ReadUInt64(content.RootElement
            .GetProperty("smallerRoundVote")
            .GetProperty("proposedBlockInfo")
            .GetProperty("round"));
        ulong largerRound = ReadUInt64(content.RootElement
            .GetProperty("largerRoundVote")
            .GetProperty("proposedBlockInfo")
            .GetProperty("round"));
        string signer = content.RootElement.GetProperty("signer").GetString()!;

        Assert.That(smallerRound, Is.EqualTo(baselineQcRound));
        Assert.That(largerRound, Is.EqualTo(incomingVoteRound));
        Assert.That(signer, Is.EqualTo(signerKey.Address.ToString()).IgnoreCase);
    }

    private static async Task<ForensicsEvent> CaptureForensicsEvent(IForensicsProcessor forensicsProcessor, Func<Task> trigger)
    {
        TaskCompletionSource<ForensicsEvent> forensicsEventWaitHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ForensicsEvent> handler = (_, args) => forensicsEventWaitHandle.TrySetResult(args);
        forensicsProcessor.ForensicsEventEmitted += handler;
        try
        {
            await trigger();
            Task completedTask = await Task.WhenAny(forensicsEventWaitHandle.Task, Task.Delay(5_000));
            if (completedTask != forensicsEventWaitHandle.Task)
            {
                Assert.Fail("Timed out waiting for forensics event.");
            }
            return await forensicsEventWaitHandle.Task;
        }
        finally
        {
            forensicsProcessor.ForensicsEventEmitted -= handler;
        }
    }

    private static ulong ReadUInt64(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetUInt64();
        }

        string value = element.GetString()!;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return ulong.Parse(value, CultureInfo.InvariantCulture);
    }

    private static void RegisterRealForensicsProcessor(ContainerBuilder builder)
    {
        builder.RegisterType<ForensicsProcessor>().As<IForensicsProcessor>().SingleInstance();
    }
}
