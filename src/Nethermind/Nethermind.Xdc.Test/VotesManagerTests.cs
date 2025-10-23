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
    public static IEnumerable<TestCaseData> VoteCases()
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

    [TestCaseSource(nameof(VoteCases))]
    public async Task VoteHandler_ExpectedCallsToCommitQc(Address[] masternodes, XdcBlockHeader header, ulong currentRound, Vote[] votes, BlockRoundInfo info, int expectedCalls)
    {
        var context = new XdcContext { CurrentRound = currentRound };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(header);

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
    public async Task VoteHandler_Returns_Early_When_Header_Missing()
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

        var voteManager = new VotesManager(context, blockTree, epochSwitchManager, snapshotManager, quorumCertificateManager,
            specProvider, signer, forensicsProcessor);

        var keysForVotes = keys.ToArray();
        for (var i = 0; i < keysForVotes.Length - 1; i++)
            await voteManager.HandleVote(BuildSignedVote(info, gap: 450, keysForVotes[i]));

        quorumCertificateManager.DidNotReceive().CommitCertificate(Arg.Any<QuorumCertificate>());

        // Now insert header and send one more
        blockTree.FindHeader(header.Hash!).Returns(header);
        await voteManager.HandleVote(BuildSignedVote(info, 450, keysForVotes[keysForVotes.Length - 1]));

        quorumCertificateManager.Received(1).CommitCertificate(Arg.Any<QuorumCertificate>());
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
}
