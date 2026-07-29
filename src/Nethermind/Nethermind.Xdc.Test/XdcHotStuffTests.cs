// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class XdcHotStuffTests
{
    /// <remarks>
    /// Simulates a vote whose own <see cref="CastVote"/> completes a QC and advances
    /// <see cref="XdcConsensusContext.CurrentRound"/> directly - the same effect
    /// <c>QuorumCertificateManager.CommitCertificate</c> has in production - without going through
    /// <c>SetNewRound</c>, so no <c>NewRoundSetEvent</c> fires and no successor round task is started
    /// by <c>XdcHotStuff.OnNewRound</c>. This reproduces the case where nothing else picks up the
    /// propose duty for the new round (e.g. a transient <c>IsSynced()</c> false at just the wrong
    /// instant would suppress the same successor in production).
    /// </remarks>
    private sealed class RoundAdvancingVotesManager(XdcConsensusContext context, ulong roundToAdvanceTo) : IVotesManager
    {
        public Task CastVote(BlockRoundInfo blockInfo)
        {
            context.CurrentRound = roundToAdvanceTo;
            return Task.CompletedTask;
        }

        public Task HandleVote(Vote vote) => Task.CompletedTask;
        public Task OnReceiveVote(Vote vote) => Task.CompletedTask;

        public bool VerifyVotingRules(BlockRoundInfo roundInfo, QuorumCertificate certificate, [NotNullWhen(false)] out string? error)
        {
            error = null;
            return true;
        }

        public bool VerifyVotingRules(XdcBlockHeader header, [NotNullWhen(false)] out string? error)
        {
            error = null;
            return true;
        }

        public bool VerifyVotingRules(Hash256 blockHash, ulong blockNumber, ulong roundNumber, QuorumCertificate qc, [NotNullWhen(false)] out string? error)
        {
            error = null;
            return true;
        }

        public IDictionary<(ulong Round, Hash256 Hash), Dictionary<Address, Vote>> GetReceivedVotes() => new Dictionary<(ulong, Hash256), Dictionary<Address, Vote>>();
    }

    [Test]
    public async Task RunRound_proposes_for_the_live_round_when_voting_advances_it_without_a_successor_task()
    {
        PrivateKeyGenerator keyGenerator = new();
        PrivateKey roundOneLeader = keyGenerator.Generate();
        PrivateKey roundTwoLeader = keyGenerator.Generate();
        // Round-robin is `round % masternodes.Length`, so with 2 masternodes round 1 and round 2 have different leaders.
        Address[] masternodes = [roundTwoLeader.Address, roundOneLeader.Address];

        const ulong votedRound = 1;
        const ulong liveRound = 2;

        XdcBlockHeader head = Build.A.XdcBlockHeader()
            .WithNumber(5)
            .WithTimestamp(0)
            .WithExtraConsensusData(new ExtraFieldsV2(votedRound, new QuorumCertificate(new BlockRoundInfo(Keccak.Zero, 0, 4), [], 0)))
            .TestObject;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(head.Hash!, head.Number).Returns(head);

        EpochSwitchInfo epochInfo = new(masternodes, [], [], new BlockRoundInfo(head.Hash!, 0, 0));
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.GetEpochSwitchInfo(Arg.Any<XdcBlockHeader>()).Returns(epochInfo);
        epochSwitchManager.IsEpochSwitchAtRound(Arg.Any<ulong>(), Arg.Any<XdcBlockHeader>()).Returns(false);

        XdcReleaseSpec spec = new()
        {
            SwitchBlock = 0,
            EpochLength = 100,
            V2Configs = [new V2ConfigParams { SwitchRound = 0, MinePeriod = 0, CertificateThreshold = 0.667 }]
        };
        // GetXdcSpec is a static extension method, not mockable directly; it falls back through
        // the real ISpecProvider.GetSpec(ForkActivation) member, which is what needs stubbing.
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        XdcConsensusContext context = new()
        {
            CurrentRound = votedRound,
            HighestQC = new QuorumCertificate(new BlockRoundInfo(head.Hash!, votedRound, head.Number), [], 0)
        };

        ISigner signer = Substitute.For<ISigner>();
        signer.Address.Returns(roundTwoLeader.Address);

        Block builtBlock = Build.A.Block.WithNumber(head.Number + 1).TestObject;
        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        blockProducer.BuildBlock(Arg.Any<BlockHeader>(), Arg.Any<Evm.Tracing.IBlockTracer>(), Arg.Any<PayloadAttributes>(), Arg.Any<IBlockProducer.Flags>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Block?>(builtBlock));

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);

        XdcHotStuff hotStuff = new(
            blockTree,
            context,
            specProvider,
            blockProducer,
            epochSwitchManager,
            Substitute.For<IMasternodesCalculator>(),
            new RoundAdvancingVotesManager(context, liveRound),
            signer,
            Substitute.For<ITimeoutTimer>(),
            new ManualTimestamper(),
            Substitute.For<IBlockProcessingQueue>(),
            stateReader,
            LimboLogs.Instance);

        TaskCompletionSource<Block> blockProducedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        hotStuff.BlockProduced += (_, e) => blockProducedTcs.TrySetResult(e.Block);

        // CurrentRound is 0 here so Start() only flips the running flag and subscribes events,
        // without racing its own auto-proposal path against the round task started below.
        context.CurrentRound = 0;
        hotStuff.Start();
        context.CurrentRound = votedRound;

        // Drives the round task the same way OnNewHeadBlock/OnNewRound would.
        hotStuff.StartRoundTask(head, votedRound);

        Task finished = await Task.WhenAny(blockProducedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.That(finished, Is.EqualTo(blockProducedTcs.Task), "Timed out waiting for the round-2 proposal - voting advanced CurrentRound to 2, but the round task never re-checked the live round before deciding whether to propose.");
        Assert.That(await blockProducedTcs.Task, Is.SameAs(builtBlock));
    }
}
