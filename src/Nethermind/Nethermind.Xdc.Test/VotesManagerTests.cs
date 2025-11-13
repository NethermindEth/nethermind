// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

public class VotesManagerTests
{
    public static IEnumerable<TestCaseData> HandleVoteCases()
    {
        var keys = MakeKeys(22);
        var keysForMasternodes = keys.Take(20).ToArray();
        var extraKeys = keys.Skip(20).ToArray();
        var masternodes = keysForMasternodes.Select(k => k.Address).ToArray();

        ulong currentRound = 1;
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 450)))
            .TestObject;
        var info = new BlockRoundInfo(header.Hash!, currentRound, header.Number);

        // Base case
        yield return new TestCaseData(masternodes, header, currentRound, keysForMasternodes.Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray(), info, 1);

        // Not enough valid signers
        var votes = keysForMasternodes.Take(12).Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray();
        var extraVotes = extraKeys.Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray();
        yield return new TestCaseData(masternodes, header, currentRound, votes.Concat(extraVotes).ToArray(), info, 0);

        // Wrong gap number generates different keys for the vote pool
        var keysForVotes = keysForMasternodes.Take(14).ToArray();
        var votesWithDiffGap = new List<Vote>(capacity: keysForVotes.Length);
        for (var i = 0; i < keysForVotes.Length - 3; i++) votesWithDiffGap.Add(XdcTestHelper.BuildSignedVote(info, 450, keysForVotes[i]));
        for (var i = keysForVotes.Length - 3; i < keysForVotes.Length; i++) votesWithDiffGap.Add(XdcTestHelper.BuildSignedVote(info, 451, keysForVotes[i]));
        yield return new TestCaseData(masternodes, header, currentRound, votesWithDiffGap.ToArray(), info, 0);
    }

    [TestCaseSource(nameof(HandleVoteCases))]
    public async Task HandleVote_VariousScenarios_CommitsQcExpectedTimes(Address[] masternodes, XdcBlockHeader header, ulong currentRound, Vote[] votes, BlockRoundInfo info, int expectedCalls)
    {
        var context = new XdcConsensusContext();
        context.SetNewRound(currentRound);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<long>()).Returns(header);

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        var epochSwitchInfo = new EpochSwitchInfo(masternodes, [], [], info);
        epochSwitchManager
            .GetEpochSwitchInfo(header)
            .Returns(epochSwitchInfo);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        xdcReleaseSpec.CertThreshold.Returns(0.667);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);

        ISigner signer = Substitute.For<ISigner>();
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();

        var voteManager = new VotesManager(context, blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);

        foreach (var v in votes)
            await voteManager.HandleVote(v);

        quorumCertificateManager.Received(expectedCalls).CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    [Test]
    public async Task HandleVote_HeaderMissing_ReturnsEarly()
    {
        var keys = MakeKeys(20);
        var masternodes = keys.Select(k => k.Address).ToArray();

        ulong currentRound = 1;
        var context = new XdcConsensusContext { CurrentRound = currentRound };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 450)))
            .TestObject;

        var info = new BlockRoundInfo(header.Hash!, currentRound, header.Number);
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        var epochSwitchInfo = new EpochSwitchInfo(masternodes, [], [], info);
        epochSwitchManager
            .GetEpochSwitchInfo(header)
            .Returns(epochSwitchInfo);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        xdcReleaseSpec.CertThreshold.Returns(0.667);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);

        ISigner signer = Substitute.For<ISigner>();
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();

        var voteManager = new VotesManager(context, blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);

        for (var i = 0; i < keys.Length - 1; i++)
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
        var ctx = new XdcConsensusContext { CurrentRound = currentRound };
        VotesManager votesManager = BuildVoteManager(ctx);

        // Dummy values, we only care about the round
        var blockInfo = new BlockRoundInfo(Hash256.Zero, 6, 0);
        var key = MakeKeys(1).First();
        var vote = XdcTestHelper.BuildSignedVote(blockInfo, 450, key);
        await votesManager.HandleVote(vote);
        Assert.That(votesManager.GetVotesCount(vote),  Is.EqualTo(expectedCount));
    }

    public static IEnumerable<TestCaseData> FilterVoteCases()
    {
        var keys = MakeKeys(21);
        var masternodes = keys.Take(20).Select(k => k.Address).ToArray();
        var blockInfo = new BlockRoundInfo(Hash256.Zero, 14, 915);

        // Disqualified as the round does not match
        var vote = new Vote(blockInfo, 450);
        yield return new TestCaseData(15UL, masternodes, vote, false);

        // Invalid signature
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(blockInfo, 450, keys.Last()), false);

        // Valid message
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(blockInfo, 450, keys.First()), true);

        // If snapshot missing should return false
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(new BlockRoundInfo(Hash256.Zero, 14, 1000), 450, keys.First()), false);

    }

    [TestCaseSource(nameof(FilterVoteCases))]
    public void FilterVote(ulong currentRound, Address[] masternodes, Vote vote, bool expected)
    {
        var context = new XdcConsensusContext();
        context.SetNewRound(currentRound);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 0)))
            .TestObject;
        blockTree.Head.Returns(new Block(header));
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByBlockNumber(915, Arg.Any<IXdcReleaseSpec>())
            .Returns(new Snapshot(0, Hash256.Zero, masternodes));
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        ISigner signer = Substitute.For<ISigner>();
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();

        var voteManager = new VotesManager(context, blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);

        Assert.That(voteManager.FilterVote(vote), Is.EqualTo(expected));
    }


    [TestCase(5UL, 4UL, false)] // Current round different from blockInfoRound
    [TestCase(5UL, 5UL, true)]  // No LockQc
    public void VerifyVotingRules_FirstChecks_ReturnsExpected(ulong currentRound, ulong blockInfoRound, bool expected)
    {
        var ctx = new XdcConsensusContext { CurrentRound = currentRound };
        VotesManager votesManager = BuildVoteManager(ctx);

        var blockInfo = new BlockRoundInfo(Hash256.Zero, blockInfoRound, 100);
        var qc = new QuorumCertificate(blockInfo, null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc), Is.EqualTo(expected));
    }

    [TestCase]
    public async Task VerifyVotingRules_RoundWasVotedOn_ReturnsFalse()
    {
        var ctx = new XdcConsensusContext { CurrentRound = 1 };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree
            .FindHeader(Arg.Any<Hash256>())
            .Returns(Build.A.XdcBlockHeader().TestObject);
        VotesManager votesManager = BuildVoteManager(ctx, blockTree);

        var blockInfo = new BlockRoundInfo(Hash256.Zero, 1, 100);
        var qc = new QuorumCertificate(blockInfo, null, 0);
        await votesManager.CastVote(blockInfo);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc), Is.False);
    }

    [Test]
    public void VerifyVotingRules_QcNewerThanLockQc_ReturnsTrue()
    {
        var lockQc = new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 4, 99), null, 0);
        var ctx = new XdcConsensusContext { CurrentRound = 5, LockQC = lockQc };
        VotesManager votesManager = BuildVoteManager(ctx);

        var blockInfo = new BlockRoundInfo(Hash256.Zero, 5, 100);
        var qc = new QuorumCertificate(blockInfo, null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc), Is.True);
    }

    public static IEnumerable<TestCaseData> ExtendingFromAncestorCases()
    {
        XdcBlockHeader[] headers = GenerateBlockHeaders(3, 99);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        var headerByHash = headers.ToDictionary(h => h.Hash!, h => h);

        XdcBlockHeader nonRelatedHeader = Build.A.XdcBlockHeader().WithNumber(99).TestObject;
        nonRelatedHeader.Hash ??= nonRelatedHeader.CalculateHash().ToHash256();
        headerByHash[nonRelatedHeader.Hash] = nonRelatedHeader;

        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(args =>
        {
            var hash = (Hash256)args[0];
            return headerByHash.TryGetValue(hash, out var header) ? header : null;
        });

        var blockInfo = new BlockRoundInfo(headers[2].Hash!, 5, headers[2].Number);

        var ancestorQc = new QuorumCertificate(new BlockRoundInfo(headers[0].Hash!, 3, headers[0].Number), null, 0);
        yield return new TestCaseData(blockTree, ancestorQc, blockInfo, true);

        var nonRelatedQc = new QuorumCertificate(new BlockRoundInfo(nonRelatedHeader.Hash, 3, nonRelatedHeader.Number), null, 0);
        yield return new TestCaseData(blockTree, nonRelatedQc, blockInfo, false);
    }

    [TestCaseSource(nameof(ExtendingFromAncestorCases))]
    public void VerifyVotingRules_CheckExtendingFromAncestor_ReturnsExpected(IBlockTree tree, QuorumCertificate lockQc, BlockRoundInfo blockInfo, bool expected)
    {
        var ctx = new XdcConsensusContext { CurrentRound = 5, LockQC = lockQc };
        VotesManager votesManager = BuildVoteManager(ctx, tree);
        var qc = new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 3, 99), null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc), Is.EqualTo(expected));
    }

    private static PrivateKey[] MakeKeys(int n)
    {
        var keyBuilder = new PrivateKeyGenerator();
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
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();

        return new VotesManager(ctx, blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);
    }

    private static XdcBlockHeader[] GenerateBlockHeaders(int n, long blockNumber)
    {
        var headers = new XdcBlockHeader[n];
        var parentHash = Hash256.Zero;
        var number = blockNumber;
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
