// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Xdc.Test.Helpers;

namespace Nethermind.Xdc.Test.ModuleTests;

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
        yield return new TestCaseData(masternodes, header, currentRound, keysForMasternodes.Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray(), info, 1)
            .SetName("BaseCase");

        // Not enough valid signers
        Vote[] votes = keysForMasternodes.Take(12).Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray();
        Vote[] extraVotes = extraKeys.Select(k => XdcTestHelper.BuildSignedVote(info, 450, k)).ToArray();
        yield return new TestCaseData(masternodes, header, currentRound, votes.Concat(extraVotes).ToArray(), info, 0)
            .SetName("NotEnoughValidSigners");

        // Wrong gap number generates different keys for the vote pool
        PrivateKey[] keysForVotes = keysForMasternodes.Take(14).ToArray();
        List<Vote> votesWithDiffGap = new(capacity: keysForVotes.Length);
        for (int i = 0; i < keysForVotes.Length - 3; i++) votesWithDiffGap.Add(XdcTestHelper.BuildSignedVote(info, 450, keysForVotes[i]));
        for (int i = keysForVotes.Length - 3; i < keysForVotes.Length; i++) votesWithDiffGap.Add(XdcTestHelper.BuildSignedVote(info, 451, keysForVotes[i]));
        yield return new TestCaseData(masternodes, header, currentRound, votesWithDiffGap.ToArray(), info, 0)
            .SetName("WrongGapNumber");

        //N byte-distinct votes but only N-1 unique addresses (keys[0] signs twice via ECDSA malleability)
        Vote[] legitimateVotes = [.. keysForMasternodes.Take(quorumCount - 1).Select(k => XdcTestHelper.BuildSignedVote(info, 450, k))];
        Signature malleableSig = XdcTestHelper.CreateMalleableSignature(legitimateVotes[0].Signature!);
        Vote malleableVote = new(info, 450) { Signature = malleableSig, Signer = legitimateVotes[0].Signer };
        yield return new TestCaseData(masternodes, header, currentRound, (Vote[])[.. legitimateVotes, malleableVote], info, 0)
            .SetName("MalleableDuplicateSigner");
    }

    [TestCaseSource(nameof(HandleVoteCases))]
    public async Task HandleVote_VariousScenarios_CommitsQcExpectedTimes(Address[] masternodes, XdcBlockHeader header, ulong currentRound, Vote[] votes, BlockRoundInfo info, int expectedCalls)
    {
        XdcConsensusContext context = new();
        context.SetNewRound(currentRound);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<ulong?>()).Returns(header);

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
            specProvider, signer, forensicsProcessor, NullLogManager.Instance);

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
            specProvider, signer, forensicsProcessor, NullLogManager.Instance);

        for (int i = 0; i < keys.Length - 1; i++)
            await voteManager.HandleVote(XdcTestHelper.BuildSignedVote(info, gap: 450, keys[i]));

        quorumCertificateManager.DidNotReceive().CommitCertificate(Arg.Any<QuorumCertificate>());

        // Now insert header and send one more
        blockTree.FindHeader(header.Hash!, Arg.Any<ulong?>()).Returns(header);
        await voteManager.HandleVote(XdcTestHelper.BuildSignedVote(info, 450, keys.Last()));

        quorumCertificateManager.Received(1).CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    [TestCase(7UL, 0)]
    [TestCase(6UL, 1)]
    [TestCase(5UL, 1)]
    public async Task HandleVote_VoteRoundOlderThanCurrentRound_RejectsVote(ulong currentRound,
        long expectedCount)
    {
        XdcConsensusContext ctx = new() { CurrentRound = currentRound };
        VotesManager votesManager = new VoteManagerBuilder { Context = ctx }.Build();

        // Dummy values, we only care about the round
        BlockRoundInfo blockInfo = new(Hash256.Zero, 6, 0);
        PrivateKey key = MakeKeys(1).First();
        Vote vote = XdcTestHelper.BuildSignedVote(blockInfo, 450, key);
        await votesManager.HandleVote(vote);
        Assert.That(votesManager.GetVotesCount(vote), Is.EqualTo(expectedCount));
    }

    public static IEnumerable<TestCaseData> VerifyVoteCases()
    {
        PrivateKey[] keys = MakeKeys(21);
        Address[] masternodes = keys.Take(20).Select(k => k.Address).ToArray();
        BlockRoundInfo blockInfo = new(Hash256.Zero, 14, 915);

        // Signer not in masternodes
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(blockInfo, 450, keys.Last()), false)
            .SetName("InvalidSignature");

        // Valid message
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(blockInfo, 450, keys.First()), true)
            .SetName("ValidMessage");

        // Snapshot missing for the given gap number
        yield return new TestCaseData(14UL, masternodes, XdcTestHelper.BuildSignedVote(blockInfo, 1350, keys.First()), false)
            .SetName("SnapshotMissing");
    }

    [TestCaseSource(nameof(VerifyVoteCases))]
    public async Task VerifyVote_OnReceiveVote_AcceptsOrRejectsVote(ulong currentRound, Address[] masternodes, Vote vote, bool expected)
    {
        XdcConsensusContext context = new();
        context.SetNewRound(currentRound);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 0)))
            .WithNumber(vote.ProposedBlockInfo.BlockNumber)
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
            specProvider, signer, forensicsProcessor, NullLogManager.Instance);

        await voteManager.OnReceiveVote(vote);

        Assert.That(voteManager.GetVotesCount(vote), Is.EqualTo(expected ? 1L : 0L));
    }


    [TestCase(5UL, 4UL, false)] // Current round different from blockInfoRound
    [TestCase(5UL, 5UL, true)]  // No LockQc
    public void VerifyVotingRules_FirstChecks_ReturnsExpected(ulong currentRound, ulong blockInfoRound, bool expected)
    {
        XdcConsensusContext ctx = new() { CurrentRound = currentRound };
        VotesManager votesManager = new VoteManagerBuilder { Context = ctx }.Build();

        BlockRoundInfo blockInfo = new(Hash256.Zero, blockInfoRound, 100);
        QuorumCertificate qc = new(blockInfo, null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc, out _), Is.EqualTo(expected));
    }

    [TestCase]
    public async Task VerifyVotingRules_RoundWasVotedOn_ReturnsFalse()
    {
        XdcConsensusContext ctx = new() { CurrentRound = 1 };
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree
            .FindHeader(Arg.Any<Hash256>())
            .Returns(header);
        blockTree.Head.Returns(new Block(header));
        VotesManager votesManager = new VoteManagerBuilder { Context = ctx, BlockTree = blockTree }.Build();

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
        VotesManager votesManager = new VoteManagerBuilder { Context = ctx }.Build();

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
        yield return new TestCaseData(blockTree, ancestorQc, blockInfo, true)
            .SetName("AncestorQc");

        QuorumCertificate nonRelatedQc = new(new BlockRoundInfo(nonRelatedHeader.Hash, 3, nonRelatedHeader.Number), null, 0);
        yield return new TestCaseData(blockTree, nonRelatedQc, blockInfo, false)
            .SetName("NonRelatedQc");
    }

    [TestCaseSource(nameof(ExtendingFromAncestorCases))]
    public void VerifyVotingRules_CheckExtendingFromAncestor_ReturnsExpected(IBlockTree tree, QuorumCertificate lockQc, BlockRoundInfo blockInfo, bool expected)
    {
        XdcConsensusContext ctx = new() { CurrentRound = 5, LockQC = lockQc };
        VotesManager votesManager = new VoteManagerBuilder { Context = ctx, BlockTree = tree }.Build();
        QuorumCertificate qc = new(new BlockRoundInfo(Hash256.Zero, 3, 99), null, 0);

        Assert.That(votesManager.VerifyVotingRules(blockInfo, qc, out _), Is.EqualTo(expected));
    }

    [Test]
    public async Task OnNewBlock_VotesArrivedBeforeBlock_BuildsQcWhenBlockArrives()
    {
        PrivateKey[] keys = MakeKeys(20);
        Address[] masternodes = keys.Select(k => k.Address).ToArray();
        ulong currentRound = 1;
        XdcConsensusContext context = new();
        context.SetNewRound(currentRound);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 450)))
            .TestObject;
        BlockRoundInfo info = new(header.Hash!, currentRound, header.Number);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<long>()).Returns((XdcBlockHeader?)null);

        IEpochSwitchManager esm = Substitute.For<IEpochSwitchManager>();
        esm.GetEpochSwitchInfo(header).Returns(new EpochSwitchInfo(masternodes, [], [], info));

        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.CertificateThreshold.Returns(0.667);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        IQuorumCertificateManager qcm = Substitute.For<IQuorumCertificateManager>();

        VotesManager voteManager = new VoteManagerBuilder
        {
            Context = context,
            BlockTree = blockTree,
            EpochSwitchManager = esm,
            SpecProvider = specProvider,
            QuorumCertificateManager = qcm
        }.Build();

        foreach (PrivateKey key in keys)
            await voteManager.HandleVote(XdcTestHelper.BuildSignedVote(info, 450, key));

        qcm.DidNotReceive().CommitCertificate(Arg.Any<QuorumCertificate>());

        blockTree.NewSuggestedBlock += Raise.EventWith(new BlockEventArgs(new Block(header)));

        qcm.Received(1).CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    [Test]
    public async Task OnNewBlock_QcAlreadyBuiltByHandleVote_DoesNotBuildAgain()
    {
        PrivateKey[] keys = MakeKeys(20);
        Address[] masternodes = keys.Select(k => k.Address).ToArray();
        ulong currentRound = 1;
        XdcConsensusContext context = new();
        context.SetNewRound(currentRound);

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(currentRound, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 450)))
            .TestObject;
        BlockRoundInfo info = new(header.Hash!, currentRound, header.Number);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<long>()).Returns(header);

        IEpochSwitchManager esm = Substitute.For<IEpochSwitchManager>();
        esm.GetEpochSwitchInfo(header).Returns(new EpochSwitchInfo(masternodes, [], [], info));

        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.CertificateThreshold.Returns(0.667);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        IQuorumCertificateManager qcm = Substitute.For<IQuorumCertificateManager>();

        VotesManager voteManager = new VoteManagerBuilder
        {
            Context = context,
            BlockTree = blockTree,
            EpochSwitchManager = esm,
            SpecProvider = specProvider,
            QuorumCertificateManager = qcm
        }.Build();

        foreach (PrivateKey key in keys)
            await voteManager.HandleVote(XdcTestHelper.BuildSignedVote(info, 450, key));

        qcm.Received(1).CommitCertificate(Arg.Any<QuorumCertificate>());

        blockTree.NewSuggestedBlock += Raise.EventWith(new BlockEventArgs(new Block(header)));

        qcm.Received(1).CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    [Test]
    public void OnNewBlock_NoVotesInPool_DoesNothing()
    {
        XdcConsensusContext context = new();
        context.SetNewRound(1);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IQuorumCertificateManager quorumCertificateManager = Substitute.For<IQuorumCertificateManager>();

        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithExtraConsensusData(new ExtraFieldsV2(1, new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), null, 0)))
            .TestObject;

        VotesManager votesManager = new VoteManagerBuilder
        {
            Context = context,
            BlockTree = blockTree,
            QuorumCertificateManager = quorumCertificateManager
        }.Build();

        blockTree.NewSuggestedBlock += Raise.EventWith(new BlockEventArgs(new Block(header)));

        quorumCertificateManager.DidNotReceive().CommitCertificate(Arg.Any<QuorumCertificate>());
    }

    // headNumber=100, currentRound=14, _maxBlockDistance=7, _maxRoundDistance=7
    // Round must be >= currentRound for accepted cases so HandleVote adds to pool
    [TestCase(107L, 14UL, 1L, TestName = "BlockDistanceSeven_Accepted")]
    [TestCase(108L, 14UL, 0L, TestName = "BlockDistanceEight_Rejected")]
    [TestCase(100L, 21UL, 1L, TestName = "RoundDistanceSeven_Accepted")]
    [TestCase(100L, 22UL, 0L, TestName = "RoundDistanceEight_Rejected")]
    public async Task OnReceiveVote_DistanceGuards_AcceptsOrRejectsVote(long voteBlockNumber, ulong voteRound, long expectedCount)
    {
        const ulong currentRound = 14;
        const long headNumber = 100;

        PrivateKey[] keys = MakeKeys(1);
        Address[] masternodes = keys.Select(k => k.Address).ToArray();
        BlockRoundInfo blockInfo = new(Hash256.Zero, voteRound, voteBlockNumber);
        Vote vote = XdcTestHelper.BuildSignedVote(blockInfo, 450, keys[0]);

        XdcConsensusContext context = new();
        context.SetNewRound(currentRound);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(new Block(Build.A.XdcBlockHeader().WithNumber(headNumber).TestObject));

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(450).Returns(new Snapshot(0, Hash256.Zero, masternodes));

        VotesManager voteManager = new VoteManagerBuilder
        {
            Context = context,
            BlockTree = blockTree,
            SnapshotManager = snapshotManager
        }.Build();

        await voteManager.OnReceiveVote(vote);

        Assert.That(voteManager.GetVotesCount(vote), Is.EqualTo(expectedCount));
    }

    [Test]
    public async Task OnReceiveVote_ValidVoteForOldRound_IsValidatedBroadcastAndNotAccumulatedInPool()
    {
        const ulong currentRound = 14;
        const ulong voteRound = 10; // distance=4, within _maxRoundDistance=7

        PrivateKey[] keys = MakeKeys(1);
        Address[] masternodes = keys.Select(k => k.Address).ToArray();
        BlockRoundInfo blockInfo = new(Hash256.Zero, voteRound, 100);
        Vote vote = XdcTestHelper.BuildSignedVote(blockInfo, 450, keys[0]);

        XdcConsensusContext context = new();
        context.SetNewRound(currentRound);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(new Block(Build.A.XdcBlockHeader().WithNumber(100).TestObject));

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(450).Returns(new Snapshot(0, Hash256.Zero, masternodes));

        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();

        VotesManager voteManager = new VoteManagerBuilder
        {
            Context = context,
            BlockTree = blockTree,
            SnapshotManager = snapshotManager,
            SyncPeerPool = syncPeerPool
        }.Build();

        await voteManager.OnReceiveVote(vote);

        // Broadcast happens even for old rounds (helps lagging peers catch up)
        _ = syncPeerPool.Received(1).AllPeers;
        // Valid signature and in masternodes, but HandleVote skips pool accumulation for old rounds
        Assert.That(voteManager.GetVotesCount(vote), Is.EqualTo(0L));
    }

    private static PrivateKey[] MakeKeys(int n)
    {
        PrivateKeyGenerator keyBuilder = new();
        PrivateKey[] keys = keyBuilder.Generate(n).ToArray();
        return keys;
    }

    private class VoteManagerBuilder
    {
        public IXdcConsensusContext Context { get; set; } = new XdcConsensusContext();
        public IBlockTree BlockTree { get; set; } = Substitute.For<IBlockTree>();
        public IEpochSwitchManager EpochSwitchManager { get; set; } = DefaultEpochSwitchManager();
        public ISnapshotManager SnapshotManager { get; set; } = Substitute.For<ISnapshotManager>();
        public IQuorumCertificateManager QuorumCertificateManager { get; set; } = Substitute.For<IQuorumCertificateManager>();
        public ISpecProvider SpecProvider { get; set; } = DefaultSpecProvider();
        public ISigner Signer { get; set; } = DefaultSigner();
        public IForensicsProcessor ForensicsProcessor { get; set; } = Substitute.For<IForensicsProcessor>();
        public ISyncPeerPool SyncPeerPool { get; set; } = Substitute.For<ISyncPeerPool>();

        public VotesManager Build() => new(Context, SyncPeerPool, BlockTree, EpochSwitchManager,
            SnapshotManager, QuorumCertificateManager, SpecProvider, Signer, ForensicsProcessor, NullLogManager.Instance);

        private static IEpochSwitchManager DefaultEpochSwitchManager()
        {
            IEpochSwitchManager esm = Substitute.For<IEpochSwitchManager>();
            esm.GetEpochSwitchInfo(Arg.Any<Hash256>())
                .Returns(new EpochSwitchInfo([], [], [], new BlockRoundInfo(Hash256.Zero, 0, 0)));
            return esm;
        }

        private static ISpecProvider DefaultSpecProvider()
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new XdcReleaseSpec { V2Configs = [new V2ConfigParams()] });
            return specProvider;
        }

        private static ISigner DefaultSigner()
        {
            ISigner signer = Substitute.For<ISigner>();
            signer.Address.Returns(TestItem.AddressA);
            signer.TrySign(in Arg.Any<ValueHash256>(), out Arg.Any<Signature>())
                .Returns(call => { call[1] = new Signature(new byte[65]); return true; });
            return signer;
        }
    }

    private static XdcBlockHeader[] GenerateBlockHeaders(int n, ulong blockNumber)
    {
        XdcBlockHeader[] headers = new XdcBlockHeader[n];
        Hash256 parentHash = Hash256.Zero;
        ulong number = blockNumber;
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
