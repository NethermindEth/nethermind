// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Synchronization.Peers;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class VotesManagerTests
{
    public static IEnumerable<TestCaseData> HandleVoteCases()
    {
        PrivateKey[] keys = MakeKeys(22);
        PrivateKey[] keysForMasternodes = keys.Take(20).ToArray();
        PrivateKey[] extraKeys = keys.Skip(20).ToArray();
        Address[] masternodes = keysForMasternodes.Select(k => k.Address).ToArray();
        int quorumCount = (int)Math.Ceiling(keysForMasternodes.Length * 0.667);

        ulong currentRound = 1;
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 450)))
            .TestObject;
        BlockRoundInfo info = new(header.Hash!, currentRound, header.Number);

        // Base case
        yield return new TestCaseData(masternodes, header, currentRound, keysForMasternodes.Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray(), info, 1);

        // Not enough valid signers
        Vote[] votes = keysForMasternodes.Take(12).Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray();
        Vote[] extraVotes = extraKeys.Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray();
        yield return new TestCaseData(masternodes, header, currentRound, votes.Concat(extraVotes).ToArray(), info, 0);

        // Wrong gap number generates different keys for the vote pool
        PrivateKey[] keysForVotes = keysForMasternodes.Take(14).ToArray();
        List<Vote> votesWithDiffGap = new(capacity: keysForVotes.Length);
        for (int i = 0; i < keysForVotes.Length - 3; i++) votesWithDiffGap.Add(XdcTestHelper.BuildSignedVote(info, 450, keysForVotes[i]));
        for (int i = keysForVotes.Length - 3; i < keysForVotes.Length; i++) votesWithDiffGap.Add(XdcTestHelper.BuildSignedVote(info, 451, keysForVotes[i]));
        yield return new TestCaseData(masternodes, header, currentRound, votesWithDiffGap.ToArray(), info, 0);

        //N byte-distinct votes but only N-1 unique addresses (keys[0] signs twice via ECDSA malleability)
        Vote[] legitimateVotes = [.. keysForMasternodes.Take(quorumCount - 1).Select(k => XdcTestHelper.BuildSignedVote(info, 450, k))];
        Signature malleableSig = XdcTestHelper.CreateMalleableSignature(legitimateVotes[0].Signature!);
        Vote malleableVote = new(info, 450) { Signature = malleableSig, Signer = legitimateVotes[0].Signer };
        yield return new TestCaseData(masternodes, header, currentRound, (Vote[])[.. legitimateVotes, malleableVote], info, 0);
    }

    [TestCaseSource(nameof(HandleVoteCases))]
    public async Task HandleVote_VariousScenarios_CommitsQcExpectedTimes(Address[] masternodes, XdcBlockHeader header, ulong currentRound, Vote[] votes, BlockRoundInfo info, int expectedCalls)
    {
        XdcConsensusContext context = new();
        context.SetNewRound(currentRound);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<long>()).Returns(header);

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        EpochSwitchInfo epochSwitchInfo = new(masternodes, [], [], info);
        epochSwitchManager
            .GetEpochSwitchInfo(header)
            .Returns(epochSwitchInfo);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        xdcReleaseSpec.CertificateThreshold.Returns(0.667);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);

        ISigner signer = Substitute.For<ISigner>();
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();

        VotesManager voteManager = new(context, Substitute.For<ISyncPeerPool>(), blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);

        foreach (Vote v in votes)
            await voteManager.HandleVote(v);

        quorumCertificateManager.Received(expectedCalls).CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    [Test]
    public async Task HandleVote_HeaderMissing_ReturnsEarly()
    {
        PrivateKey[] keys = MakeKeys(20);
        Address[] masternodes = keys.Select(k => k.Address).ToArray();

        ulong currentRound = 1;
        XdcConsensusContext context = new() { CurrentRound = currentRound };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 450)))
            .TestObject;

        BlockRoundInfo info = new(header.Hash!, currentRound, header.Number);
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        EpochSwitchInfo epochSwitchInfo = new(masternodes, [], [], info);
        epochSwitchManager
            .GetEpochSwitchInfo(header)
            .Returns(epochSwitchInfo);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        xdcReleaseSpec.CertificateThreshold.Returns(0.667);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);

        ISigner signer = Substitute.For<ISigner>();
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();

        VotesManager voteManager = new(context, Substitute.For<ISyncPeerPool>(), blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);

        for (int i = 0; i < keys.Length - 1; i++)
            await voteManager.HandleVote(XdcTestHelper.BuildSignedVote(info, gap: 450, keys[i]));

        quorumCertificateManager.DidNotReceive().CommitCertificate(Arg.Any<QuorumCertificate>());

        // Now insert header and send one more
        blockTree.FindHeader(header.Hash!, Arg.Any<long>()).Returns(header);
        await voteManager.HandleVote(XdcTestHelper.BuildSignedVote(info, 450, keys.Last()));

        quorumCertificateManager.Received(1).CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    [TestCase(7UL, 0)]
    [TestCase(6UL, 1)]
    [TestCase(5UL, 1)]
    [TestCase(4UL, 0)]
    public async Task HandleVote_MsgRoundDifferentValues_ReturnsEarlyIfTooFarFromCurrentRound(ulong currentRound,
        long expectedCount)
    {
        XdcConsensusContext ctx = new() { CurrentRound = currentRound };
        VotesManager votesManager = BuildVoteManager(ctx);

        // Dummy values, we only care about the round
        BlockRoundInfo blockInfo = new(Hash256.Zero, 6, 0);
        PrivateKey key = MakeKeys(1).First();
        Vote vote = XdcTestHelper.BuildSignedVote(blockInfo, 450, key);
        await votesManager.HandleVote(vote);
        Assert.That(votesManager.GetVotesCount(vote), Is.EqualTo(expectedCount));
    }

    public static IEnumerable<TestCaseData> FilterVoteCases()
    {
        PrivateKey[] keys = MakeKeys(21);
        Address[] masternodes = keys.Take(20).Select(k => k.Address).ToArray();
        BlockRoundInfo blockInfo = new(Hash256.Zero, 14, 915);

        // Disqualified as the round does not match
        Vote vote = new(blockInfo, 450);
        yield return new TestCaseData(15UL, masternodes, vote, false);

        // Invalid signature
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(blockInfo, 450, keys.Last()), false);

        // Valid message
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(blockInfo, 450, keys.First()), true);

        // If snapshot missing should return false
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(blockInfo, 1350, keys.First()), false);

    }

    [TestCaseSource(nameof(FilterVoteCases))]
    public void FilterVote(ulong currentRound, Address[] masternodes, Vote vote, bool expected)
    {
        XdcConsensusContext context = new();
        context.SetNewRound(currentRound);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 0)))
            .TestObject;
        blockTree.Head.Returns(new Block(header));
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(450)
            .Returns(new Snapshot(0, Hash256.Zero, masternodes));
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        ISigner signer = Substitute.For<ISigner>();
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();

        VotesManager voteManager = new(context, Substitute.For<ISyncPeerPool>(), blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);

        Assert.That(voteManager.FilterVote(vote), Is.EqualTo(expected));
    }


    [TestCase(5UL, 4UL, false)] // Current round different from blockInfoRound
    [TestCase(5UL, 5UL, true)]  // No LockQc
    public void VerifyVotingRules_FirstChecks_ReturnsExpected(ulong currentRound, ulong blockInfoRound, bool expected)
    {
        XdcConsensusContext ctx = new() { CurrentRound = currentRound };
        VotesManager votesManager = BuildVoteManager(ctx);

        BlockRoundInfo blockInfo = new(Hash256.Zero, blockInfoRound, 100);
        QuorumCertificate qc = new(blockInfo, null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc, out _), Is.EqualTo(expected));
    }

    [TestCase]
    public async Task VerifyVotingRules_RoundWasVotedOn_ReturnsFalse()
    {
        XdcConsensusContext ctx = new() { CurrentRound = 1 };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree
            .FindHeader(Arg.Any<Hash256>())
            .Returns(Build.A.XdcBlockHeader().TestObject);
        VotesManager votesManager = BuildVoteManager(ctx, blockTree);

        BlockRoundInfo blockInfo = new(Hash256.Zero, 1, 100);
        QuorumCertificate qc = new(blockInfo, null, 0);
        await votesManager.CastVote(blockInfo);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc, out _), Is.False);
    }

    [Test]
    public void VerifyVotingRules_QcNewerThanLockQc_ReturnsTrue()
    {
        QuorumCertificate lockQc = new(new BlockRoundInfo(Hash256.Zero, 4, 99), null, 0);
        XdcConsensusContext ctx = new() { CurrentRound = 5, LockQC = lockQc };
        VotesManager votesManager = BuildVoteManager(ctx);

        BlockRoundInfo blockInfo = new(Hash256.Zero, 5, 100);
        QuorumCertificate qc = new(blockInfo, null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc, out _), Is.True);
    }

    public static IEnumerable<TestCaseData> ExtendingFromAncestorCases()
    {
        XdcBlockHeader[] headers = GenerateBlockHeaders(3, 99);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Dictionary<Hash256, XdcBlockHeader> headerByHash = headers.ToDictionary(h => h.Hash!, h => h);

        XdcBlockHeader nonRelatedHeader = Build.A.XdcBlockHeader().WithNumber(99).TestObject;
        nonRelatedHeader.Hash ??= nonRelatedHeader.CalculateHash().ToHash256();
        headerByHash[nonRelatedHeader.Hash] = nonRelatedHeader;

        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(args =>
        {
            Hash256 hash = (Hash256)args[0];
            return headerByHash.TryGetValue(hash, out XdcBlockHeader? header) ? header : null;
        });

        BlockRoundInfo blockInfo = new(headers[2].Hash!, 5, headers[2].Number);

        QuorumCertificate ancestorQc = new(new BlockRoundInfo(headers[0].Hash!, 3, headers[0].Number), null, 0);
        yield return new TestCaseData(blockTree, ancestorQc, blockInfo, true);

        QuorumCertificate nonRelatedQc = new(new BlockRoundInfo(nonRelatedHeader.Hash, 3, nonRelatedHeader.Number), null, 0);
        yield return new TestCaseData(blockTree, nonRelatedQc, blockInfo, false);
    }

    [TestCaseSource(nameof(ExtendingFromAncestorCases))]
    public void VerifyVotingRules_CheckExtendingFromAncestor_ReturnsExpected(IBlockTree tree, QuorumCertificate lockQc, BlockRoundInfo blockInfo, bool expected)
    {
        XdcConsensusContext ctx = new() { CurrentRound = 5, LockQC = lockQc };
        VotesManager votesManager = BuildVoteManager(ctx, tree);
        QuorumCertificate qc = new(new BlockRoundInfo(Hash256.Zero, 3, 99), null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc, out _), Is.EqualTo(expected));
    }

    private static PrivateKey[] MakeKeys(int n)
    {
        PrivateKeyGenerator keyBuilder = new();
        PrivateKey[] keys = keyBuilder.Generate(n).ToArray();
        return keys;
    }

    private static VotesManager BuildVoteManager(IXdcConsensusContext ctx, IBlockTree? blockTree = null)
    {
        blockTree ??= Substitute.For<IBlockTree>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager
            .GetEpochSwitchInfo(Arg.Any<Hash256>())
            .Returns(new EpochSwitchInfo([], [], [], new BlockRoundInfo(Hash256.Zero, 0, 0)));
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new XdcReleaseSpec()
        {
            V2Configs = [new V2ConfigParams()]
        });
        ISigner signer = Substitute.For<ISigner>();
        signer.Address.Returns(TestItem.AddressA);
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();

        return new VotesManager(ctx, Substitute.For<ISyncPeerPool>(), blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);
    }

    private static XdcBlockHeader[] GenerateBlockHeaders(int n, long blockNumber)
    {
        XdcBlockHeader[] headers = new XdcBlockHeader[n];
        Hash256 parentHash = Hash256.Zero;
        long number = blockNumber;
        for (int i = 0; i < n; i++, number++)
        {
            headers[i] = Build.A.XdcBlockHeader()
                .WithNumber(number)
                .WithParentHash(parentHash)
                .TestObject;
            parentHash = headers[i].CalculateHash().ToHash256();
        }

        return headers;
    }
}
