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
        var (keys, _) = MakeKeys(20);
        var masternodes = keys.Select(k => k.Address).ToArray();

        ulong currentRound = 1;
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 450)))
            .TestObject;
        var info = new BlockRoundInfo(header.Hash!, currentRound, header.Number);

        // Base case
        yield return new TestCaseData(masternodes, header, currentRound, keys.Select(k => BuildSignedVote(info, 450, k)).ToArray(), info, 1);

        // Not enough valid signers
        var (extraKeys, _) = MakeKeys(2);
        var votes = keys.Take(12).Select(k => BuildSignedVote(info, 450, k)).ToArray();
        var extraVotes = extraKeys.Select(k => BuildSignedVote(info, 450, k)).ToArray();
        yield return new TestCaseData(masternodes, header, currentRound, votes.Concat(extraVotes).ToArray(), info, 0);

        // Wrong gap number generates different keys for the vote pool
        var keysForVotes = keys.Take(14).ToArray();
        var votesWithDiffGap = new List<Vote>(capacity: keysForVotes.Length);
        for (var i = 0; i < keysForVotes.Length - 3; i++) votesWithDiffGap.Add(BuildSignedVote(info, 450, keysForVotes[i]));
        for (var i = keysForVotes.Length - 3; i < keysForVotes.Length; i++) votesWithDiffGap.Add(BuildSignedVote(info, 451, keysForVotes[i]));
        yield return new TestCaseData(masternodes, header, currentRound, votesWithDiffGap.ToArray(), info, 0);
    }

    [TestCaseSource(nameof(HandleVoteCases))]
    public async Task HandleVote_VariousScenarios_CommitsQcExpectedTimes(Address[] masternodes, XdcBlockHeader header, ulong currentRound, Vote[] votes, BlockRoundInfo info, int expectedCalls)
    {
        var context = new XdcContext { CurrentRound = currentRound };
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
        IBlockInfoValidator blockInfoValidator = new BlockInfoValidator();

        var voteManager = new VotesManager(context, blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor, blockInfoValidator);

        foreach (var v in votes)
            await voteManager.HandleVote(v);

        quorumCertificateManager.Received(expectedCalls).CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    [Test]
    public async Task HandleVote_HeaderMissing_ReturnsEarly()
    {
        var (keys, _) = MakeKeys(20);
        var masternodes = keys.Select(k => k.Address).ToArray();

        ulong currentRound = 1;
        var context = new XdcContext { CurrentRound = currentRound };
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
        IBlockInfoValidator blockInfoValidator = new BlockInfoValidator();

        var voteManager = new VotesManager(context, blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor, blockInfoValidator);

        var keysForVotes = keys.ToArray();
        for (var i = 0; i < keysForVotes.Length - 1; i++)
            await voteManager.HandleVote(BuildSignedVote(info, gap: 450, keysForVotes[i]));

        quorumCertificateManager.DidNotReceive().CommitCertificate(Arg.Any<QuorumCertificate>());

        // Now insert header and send one more
        blockTree.FindHeader(header.Hash!, Arg.Any<long>()).Returns(header);
        await voteManager.HandleVote(BuildSignedVote(info, 450, keysForVotes[keysForVotes.Length - 1]));

        quorumCertificateManager.Received(1).CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    [TestCase(5UL, 5UL, 5UL, false)] // Current round already voted
    [TestCase(5UL, 4UL, 4UL, false)] // Current round different from blockInfoRound
    [TestCase(5UL, 4UL, 5UL, true)]  // No LockQc
    public void VerifyVotingRules_FirstChecks_ReturnsExpected(ulong currentRound, ulong highestVotedRound, ulong blockInfoRound, bool expected)
    {
        var ctx = new XdcContext { CurrentRound = currentRound, HighestVotedRound = highestVotedRound };
        VotesManager votesManager = BuildVoteManager(ctx);

        var blockInfo = new BlockRoundInfo(Hash256.Zero, blockInfoRound, 100);
        var qc = new QuorumCertificate(blockInfo, null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc), Is.EqualTo(expected));
    }

    [Test]
    public void VerifyVotingRules_QcNewerThanLockQc_ReturnsTrue()
    {
        var lockQc = new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 4, 99), null, 0);
        var ctx = new XdcContext { CurrentRound = 5, HighestVotedRound = 4, LockQC = lockQc };
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
        var ctx = new XdcContext { CurrentRound = 5, HighestVotedRound = 4, LockQC = lockQc };
        VotesManager votesManager = BuildVoteManager(ctx, tree);
        var qc = new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 3, 99), null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc), Is.EqualTo(expected));
    }

    private static (PrivateKey[] keys, Address[] addrs) MakeKeys(int n)
    {
        var keyBuilder = new PrivateKeyGenerator();
        PrivateKey[] keys = keyBuilder.Generate(n).ToArray();
        Address[] addrs = keys.Select(k => k.Address).ToArray();
        return (keys, addrs);
    }

    private static Vote BuildSignedVote(
        BlockRoundInfo info, ulong gap, PrivateKey key)
    {
        var decoder = new VoteDecoder();
        var ecdsa = new EthereumEcdsa(0);
        var vote = new Vote(info, gap);
        var stream = new KeccakRlpStream();
        decoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        vote.Signature = ecdsa.Sign(key, stream.GetValueHash());
        vote.Signer = key.Address;
        return vote;
    }

    private static VotesManager BuildVoteManager(XdcContext ctx, IBlockTree? blockTree = null)
    {
        blockTree ??= Substitute.For<IBlockTree>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        ISigner signer = Substitute.For<ISigner>();
        IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();
        IBlockInfoValidator blockInfoValidator = Substitute.For<IBlockInfoValidator>();

        return new VotesManager(ctx, blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor, blockInfoValidator);
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
