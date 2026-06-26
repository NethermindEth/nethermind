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

namespace Nethermind.Xdc.Test.ModuleTests;

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
        XdcBlockHeader? targetParentHeader = (XdcBlockHeader?)blockchain.BlockTree.FindHeader(targetHeader.ParentHash!);
        Assert.That(targetParentHeader, Is.Not.Null);
        Assert.That(targetParentHeader.ExtraConsensusData, Is.Not.Null);
        Assert.That(targetHeader.ExtraConsensusData, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
            Assert.That(highestCommittedQcs[0], Is.EqualTo(targetParentHeader.ExtraConsensusData.QuorumCert!));
            Assert.That(highestCommittedQcs[1], Is.EqualTo(targetHeader.ExtraConsensusData.QuorumCert!));
            Assert.That(highestCommittedQcs[2], Is.EqualTo(context.HighestQC));
        }
    }

    [Test]
    public async Task TestSetCommittedQCsInOrder()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 5, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader headerN = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader headerNMinus1 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerN.ParentHash!)!;
        XdcBlockHeader headerNMinus2 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus1.ParentHash!)!;
        XdcBlockHeader headerNMinus3 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus2.ParentHash!)!;

        QuorumCertificate[] beforeInvalidOrder = forensicsProcessor.GetHighestCommittedQcsSnapshot();

        await forensicsProcessor.SetCommittedQCs(
            [headerNMinus2, headerNMinus3],
            headerN.ExtraConsensusData!.QuorumCert!);

        QuorumCertificate[] highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        Assert.That(highestCommittedQcs, Is.EqualTo(beforeInvalidOrder));

        await forensicsProcessor.SetCommittedQCs(
            [headerNMinus2, headerNMinus1],
            headerN.ExtraConsensusData!.QuorumCert!);

        highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
            Assert.That(highestCommittedQcs[0], Is.EqualTo(headerNMinus2.ExtraConsensusData!.QuorumCert!));
            Assert.That(highestCommittedQcs[1], Is.EqualTo(headerNMinus1.ExtraConsensusData!.QuorumCert!));
            Assert.That(highestCommittedQcs[2], Is.EqualTo(headerN.ExtraConsensusData!.QuorumCert!));
        }

        await forensicsProcessor.SetCommittedQCs(
            [headerNMinus3, headerNMinus2],
            headerNMinus1.ExtraConsensusData!.QuorumCert!);

        highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
            Assert.That(highestCommittedQcs[0], Is.EqualTo(headerNMinus3.ExtraConsensusData!.QuorumCert!));
            Assert.That(highestCommittedQcs[1], Is.EqualTo(headerNMinus2.ExtraConsensusData!.QuorumCert!));
            Assert.That(highestCommittedQcs[2], Is.EqualTo(headerNMinus1.ExtraConsensusData!.QuorumCert!));
        }
    }

    [Test]
    public async Task TestForensicsMonitoring()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 15, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader headerN = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader headerNMinus1 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerN.ParentHash!)!;
        XdcBlockHeader headerNMinus2 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus1.ParentHash!)!;

        XdcBlockHeader headerNMinus3 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus2.ParentHash!)!;
        XdcBlockHeader headerNMinus4 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus3.ParentHash!)!;
        XdcBlockHeader headerNMinus5 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus4.ParentHash!)!;

        // Seed forensics baseline with an older valid committed-QC triplet.
        await forensicsProcessor.SetCommittedQCs(
            [headerNMinus5, headerNMinus4],
            headerNMinus3.ExtraConsensusData!.QuorumCert!);

        Assert.That(forensicsProcessor.GetHighestCommittedQcsSnapshot().Length, Is.EqualTo(3));

        // Feed a newer same-chain window; happy-path behavior should update committed QCs without errors.
        Assert.That(async () => await forensicsProcessor.ForensicsMonitoring(
            [headerNMinus2, headerNMinus1],
            headerN.ExtraConsensusData!.QuorumCert!), Throws.Nothing);

        QuorumCertificate[] highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
            Assert.That(highestCommittedQcs[0], Is.EqualTo(headerNMinus2.ExtraConsensusData!.QuorumCert!));
            Assert.That(highestCommittedQcs[1], Is.EqualTo(headerNMinus1.ExtraConsensusData!.QuorumCert!));
            Assert.That(highestCommittedQcs[2], Is.EqualTo(headerN.ExtraConsensusData!.QuorumCert!));
        }
    }

    [Test]
    public async Task TestForensicsMonitoringNotOnSameChainButHaveSameRoundQC()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 15, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader headerN = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader headerNMinus1 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerN.ParentHash!)!;
        XdcBlockHeader headerNMinus2 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus1.ParentHash!)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headerN.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus1.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus2.ExtraConsensusData, Is.Not.Null);
        }

        // Seed highest committed QCs from canonical tip window.
        await forensicsProcessor.SetCommittedQCs(
            [headerNMinus2, headerNMinus1],
            headerN.ExtraConsensusData!.QuorumCert!);

        // Build a fork from the same grandparent; this gives a divergent chain segment.
        Block forkBlock1 = await blockchain.AddBlockFromParent(headerNMinus2);
        XdcBlockHeader forkHeader1 = (XdcBlockHeader)forkBlock1.Header;
        Block forkBlock2 = await blockchain.AddBlockFromParent(forkHeader1);
        XdcBlockHeader forkHeader2 = (XdcBlockHeader)forkBlock2.Header;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkHeader1.ExtraConsensusData, Is.Not.Null);
            Assert.That(forkHeader2.ExtraConsensusData, Is.Not.Null);
            Assert.That(forkHeader1.Hash, Is.Not.EqualTo(headerNMinus1.Hash));
        }

        // Normalize the fork window so SetCommittedQCs validation passes:
        // qc on forkHeader1 must point to headerNMinus2.
        QuorumCertificate forkHeader1Qc = new(
            new BlockRoundInfo(
                headerNMinus2.Hash!,
                headerNMinus1.ExtraConsensusData!.QuorumCert!.ProposedBlockInfo.Round,
                headerNMinus2.Number),
            [],
            forkHeader1.ExtraConsensusData!.QuorumCert!.GapNumber);
        forkHeader1.ExtraConsensusData = new ExtraFieldsV2(forkHeader1.ExtraConsensusData.BlockRound, forkHeader1Qc);

        // Compose incoming fork QCs in ancestor->descendant order and verify the same-round condition.
        QuorumCertificate[] incomingForkQcs = [
            headerNMinus2.ExtraConsensusData!.QuorumCert!,
            forkHeader1Qc,
            forkHeader2.ExtraConsensusData!.QuorumCert!
        ];
        // Build deterministic incoming QC that points to forkHeader1, matching SetCommittedQCs expectations.
        QuorumCertificate incomingForkMonitoringQc = new(
            new BlockRoundInfo(
                forkHeader1.Hash!,
                forkHeader1.ExtraConsensusData!.BlockRound,
                forkHeader1.Number),
            [],
            forkHeader2.ExtraConsensusData!.QuorumCert!.GapNumber);
        QuorumCertificate[] highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        bool hasSameRoundQc = forensicsProcessor.TryFindQCsInSameRound(
            highestCommittedQcs,
            incomingForkQcs,
            out _,
            out _);
        Assert.That(hasSameRoundQc, Is.True);

        ForensicsEvent forensicsEvent = await CaptureForensicsEvent(
            forensicsProcessor,
            () => forensicsProcessor.ForensicsMonitoring([headerNMinus2, forkHeader1], incomingForkMonitoringQc));
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

        // Forensics monitoring always refreshes committed-QC snapshot with the incoming valid window.
        highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallerRound, Is.EqualTo(largerRound));
            Assert.That(acrossEpoch, Is.False);
            Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
            Assert.That(highestCommittedQcs[0], Is.SameAs(headerNMinus2.ExtraConsensusData!.QuorumCert!));
            Assert.That(highestCommittedQcs[1], Is.SameAs(forkHeader1Qc));
            Assert.That(highestCommittedQcs[2], Is.SameAs(incomingForkMonitoringQc));
        }
    }

    [Test]
    public async Task TestForensicsMonitoringNotOnSameChainDoNotHaveSameRoundQC()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 15, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader headerN = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader headerNMinus1 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerN.ParentHash!)!;
        XdcBlockHeader headerNMinus2 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus1.ParentHash!)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headerN.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus1.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus2.ExtraConsensusData, Is.Not.Null);
        }

        // Seed highest committed QCs with synthetic high rounds so incoming fork ancestry
        // cannot match by round in the live ProcessForensics path.
        QuorumCertificate baselineGrandParentQc = new(
            new BlockRoundInfo(
                headerNMinus2.ExtraConsensusData!.QuorumCert!.ProposedBlockInfo.Hash,
                1_000,
                headerNMinus2.ExtraConsensusData.QuorumCert!.ProposedBlockInfo.BlockNumber),
            headerNMinus2.ExtraConsensusData.QuorumCert!.Signatures,
            headerNMinus2.ExtraConsensusData.QuorumCert!.GapNumber);
        headerNMinus2.ExtraConsensusData = new ExtraFieldsV2(headerNMinus2.ExtraConsensusData.BlockRound, baselineGrandParentQc);

        QuorumCertificate baselineParentQc = new(
            new BlockRoundInfo(
                headerNMinus2.Hash!,
                1_001,
                headerNMinus2.Number),
            headerNMinus1.ExtraConsensusData!.QuorumCert!.Signatures,
            headerNMinus1.ExtraConsensusData.QuorumCert!.GapNumber);
        headerNMinus1.ExtraConsensusData = new ExtraFieldsV2(headerNMinus1.ExtraConsensusData.BlockRound, baselineParentQc);

        QuorumCertificate baselineIncomingQc = new(
            new BlockRoundInfo(
                headerNMinus1.Hash!,
                1_002,
                headerNMinus1.Number),
            headerN.ExtraConsensusData!.QuorumCert!.Signatures,
            headerN.ExtraConsensusData.QuorumCert!.GapNumber);

        await forensicsProcessor.SetCommittedQCs(
            [headerNMinus2, headerNMinus1],
            baselineIncomingQc);

        // Build a fork from the same grandparent; this gives a divergent chain segment.
        Block forkBlock1 = await blockchain.AddBlockFromParent(headerNMinus2);
        XdcBlockHeader forkHeader1 = (XdcBlockHeader)forkBlock1.Header;
        Block forkBlock2 = await blockchain.AddBlockFromParent(forkHeader1);
        XdcBlockHeader forkHeader2 = (XdcBlockHeader)forkBlock2.Header;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forkHeader1.ExtraConsensusData, Is.Not.Null);
            Assert.That(forkHeader2.ExtraConsensusData, Is.Not.Null);
            Assert.That(forkHeader1.Hash, Is.Not.EqualTo(headerNMinus1.Hash));
        }

        // Construct fork QCs with rounds intentionally far from canonical rounds.
        ulong forkRound1 = headerN.ExtraConsensusData.BlockRound + 20;
        ulong forkRound2 = forkRound1 + 1;

        QuorumCertificate forkHeader1Qc = new(
            new BlockRoundInfo(
                headerNMinus2.Hash!,
                forkRound1,
                headerNMinus2.Number),
            [],
            forkHeader1.ExtraConsensusData!.QuorumCert!.GapNumber);
        forkHeader1.ExtraConsensusData = new ExtraFieldsV2(forkHeader1.ExtraConsensusData.BlockRound, forkHeader1Qc);

        QuorumCertificate incomingForkMonitoringQc = new(
            new BlockRoundInfo(
                forkHeader1.Hash!,
                forkRound2,
                forkHeader1.Number),
            [],
            forkHeader2.ExtraConsensusData!.QuorumCert!.GapNumber);

        // Force the incoming ancestry used by ProcessForensics to be disjoint in round space
        // from highestCommittedQcs by replacing headerNMinus2 QC with a synthetic one.
        QuorumCertificate syntheticAncestorQc = new(
            new BlockRoundInfo(
                headerNMinus2.Hash!,
                forkRound1 + 100,
                headerNMinus2.Number),
            [],
            forkHeader1Qc.GapNumber);
        headerNMinus2.ExtraConsensusData = new ExtraFieldsV2(
            headerNMinus2.ExtraConsensusData!.BlockRound,
            syntheticAncestorQc);

        QuorumCertificate[] incomingForkQcs = [
            incomingForkMonitoringQc,
            forkHeader1Qc,
            syntheticAncestorQc
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
            () => forensicsProcessor.ForensicsMonitoring([headerNMinus2, forkHeader1], incomingForkMonitoringQc));
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

        // Even in the no-same-round case, committed-QC snapshot should refresh to incoming valid window.
        highestCommittedQcs = forensicsProcessor.GetHighestCommittedQcsSnapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallerRound, Is.LessThan(largerRound));
            Assert.That(acrossEpoch, Is.False);
            Assert.That(highestCommittedQcs.Length, Is.EqualTo(3));
            Assert.That(highestCommittedQcs[0], Is.SameAs(headerNMinus2.ExtraConsensusData!.QuorumCert!));
            Assert.That(highestCommittedQcs[1], Is.SameAs(forkHeader1Qc));
            Assert.That(highestCommittedQcs[2], Is.SameAs(incomingForkMonitoringQc));
        }
    }

    [Test]
    public async Task TestForensicsAcrossEpoch()
    {
        // Use a short epoch in tests so we can cross epoch boundaries with fewer blocks.
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 0, configurer: RegisterRealForensicsProcessor);
        blockchain.ChangeReleaseSpec(spec =>
        {
            spec.EpochLength = 30;
            spec.Gap = 15;
        });
        await blockchain.AddBlocks(121);

        // Build proof from QCs in two different epochs.
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader headerN = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        Assert.That(headerN.ExtraConsensusData, Is.Not.Null);

        // Walk back far enough to guarantee a different epoch.
        XdcBlockHeader oldEpochHeader = headerN;
        for (int i = 0; i < 70; i++)
        {
            oldEpochHeader = (XdcBlockHeader)blockchain.BlockTree.FindHeader(oldEpochHeader.ParentHash!)!;
        }
        Assert.That(oldEpochHeader.ExtraConsensusData, Is.Not.Null);

        QuorumCertificate oldEpochQc = oldEpochHeader.ExtraConsensusData!.QuorumCert!;
        QuorumCertificate currentQc = headerN.ExtraConsensusData!.QuorumCert!;

        ForensicsEvent forensicsEvent = await CaptureForensicsEvent(
            forensicsProcessor,
            () => forensicsProcessor.SendForensicProof(oldEpochQc, currentQc));

        Assert.That(forensicsEvent.ForensicsProof.ForensicsType, Is.EqualTo("QC"));

        using JsonDocument content = JsonDocument.Parse(forensicsEvent.ForensicsProof.Content);
        bool acrossEpoch = content.RootElement.GetProperty("acrossEpoch").GetBoolean();
        string divergingBlockHash = content.RootElement.GetProperty("divergingBlockHash").GetString()!;
        string smallerHash = content.RootElement
            .GetProperty("smallerRoundInfo")
            .GetProperty("quorumCert")
            .GetProperty("proposedBlockInfo")
            .GetProperty("hash")
            .GetString()!;
        string largerHash = content.RootElement
            .GetProperty("largerRoundInfo")
            .GetProperty("quorumCert")
            .GetProperty("proposedBlockInfo")
            .GetProperty("hash")
            .GetString()!;

        // XDC test also verifies ID composition from the emitted content.
        string expectedId = $"{divergingBlockHash}:{smallerHash}:{largerHash}";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forensicsEvent.ForensicsProof.Id, Is.EqualTo(expectedId));
            Assert.That(acrossEpoch, Is.True);
        }
    }

    [Test]
    public async Task TestVoteEquivocationSameRound()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 10, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();
        IXdcConsensusContext context = blockchain.XdcContext;

        XdcBlockHeader headerN = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader headerNMinus1 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerN.ParentHash!)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headerN.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus1.ExtraConsensusData, Is.Not.Null);
        }

        // Build a sibling fork block at the same height as headerN.
        Block forkBlock = await blockchain.AddBlockFromParent(headerNMinus1);
        XdcBlockHeader forkHeader = (XdcBlockHeader)forkBlock.Header;
        Assert.That(forkHeader.Hash, Is.Not.EqualTo(headerN.Hash));

        // Same signer, same round, different block hashes => vote equivocation scenario.
        EpochSwitchInfo epochSwitchInfo = blockchain.EpochSwitchManager.GetEpochSwitchInfo(headerN)!;
        Address currentMasternode = epochSwitchInfo.Masternodes.First();
        PrivateKey signerKey = blockchain.MasterNodeCandidates.First(candidate => candidate.Address == currentMasternode);
        ulong round = context.CurrentRound;
        ulong gap = headerN.ExtraConsensusData.QuorumCert!.GapNumber;
        BlockRoundInfo canonicalRoundInfo = new(headerN.Hash!, round, headerN.Number);
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallerRound, Is.EqualTo(round));
            Assert.That(largerRound, Is.EqualTo(round));
            Assert.That(signer, Is.EqualTo(signerKey.Address.ToString()).IgnoreCase);
        }
    }

    [Test]
    public async Task TestVoteEquivocationDifferentRound()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create(blocksToAdd: 15, configurer: RegisterRealForensicsProcessor);
        ForensicsProcessor forensicsProcessor = (ForensicsProcessor)blockchain.Container.Resolve<IForensicsProcessor>();

        XdcBlockHeader headerN = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        XdcBlockHeader headerNMinus1 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerN.ParentHash!)!;
        XdcBlockHeader headerNMinus2 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus1.ParentHash!)!;
        XdcBlockHeader headerNMinus3 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus2.ParentHash!)!;
        XdcBlockHeader headerNMinus4 = (XdcBlockHeader)blockchain.BlockTree.FindHeader(headerNMinus3.ParentHash!)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headerN.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus1.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus2.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus3.ExtraConsensusData, Is.Not.Null);
            Assert.That(headerNMinus4.ExtraConsensusData, Is.Not.Null);
        }

        // Mirror XDC test intent: set committed QCs on canonical chain, then submit
        // a vote that does not extend from the committed ancestor and has a higher round.
        EpochSwitchInfo epochSwitchInfo = blockchain.EpochSwitchManager.GetEpochSwitchInfo(headerN)!;
        Address currentMasternode = epochSwitchInfo.Masternodes.First();
        PrivateKey signerKey = blockchain.MasterNodeCandidates.First(candidate => candidate.Address == currentMasternode);
        ulong baselineQcRound = 14;
        ulong incomingVoteRound = 16;
        ulong gap = headerN.ExtraConsensusData!.QuorumCert!.GapNumber;

        Vote qcSignedVote = XdcTestHelper.BuildSignedVote(
            new BlockRoundInfo(headerNMinus1.Hash!, baselineQcRound, headerNMinus1.Number),
            gap,
            signerKey);
        QuorumCertificate baselineIncomingQc = new(
            qcSignedVote.ProposedBlockInfo,
            [qcSignedVote.Signature!],
            gap);

        await forensicsProcessor.SetCommittedQCs(
            [headerNMinus2, headerNMinus1],
            baselineIncomingQc);

        Vote incomingVote = XdcTestHelper.BuildSignedVote(
            new BlockRoundInfo(headerNMinus4.Hash!, incomingVoteRound, headerNMinus4.Number),
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(smallerRound, Is.EqualTo(baselineQcRound));
            Assert.That(largerRound, Is.EqualTo(incomingVoteRound));
            Assert.That(signer, Is.EqualTo(signerKey.Address.ToString()).IgnoreCase);
        }
    }

    private static async Task<ForensicsEvent> CaptureForensicsEvent(IForensicsProcessor forensicsProcessor, Func<Task> trigger)
    {
        TaskCompletionSource<ForensicsEvent> forensicsEventTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ForensicsEvent> handler = (_, args) => forensicsEventTcs.TrySetResult(args);
        forensicsProcessor.ForensicsEventEmitted += handler;
        try
        {
            await trigger();
            Task completedTask = await Task.WhenAny(forensicsEventTcs.Task, Task.Delay(5_000));
            if (completedTask != forensicsEventTcs.Task)
            {
                Assert.Fail("Timed out waiting for forensics event.");
            }
            return await forensicsEventTcs.Task;
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

    private static void RegisterRealForensicsProcessor(ContainerBuilder builder) => builder.RegisterType<ForensicsProcessor>().As<IForensicsProcessor>().SingleInstance();
}
