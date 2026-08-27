// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

[TestFixture]
public class XdcMasternodeEthModuleTests
{
    private const ulong EpochLength = 900;
    private const ulong MergeSignRange = 15;

    // A head at round 1000 sits in epoch 1, whose checkpoint is block 900.
    private const ulong HeadNumber = 1000;
    private const ulong CurrentEpoch = 1;
    private const ulong CheckpointNumber = 900;

    // Checkpoints 1800s apart make a year 17,520 epochs long. Staking 4.38e9 XDC against the
    // 2500 XDC per epoch that reaches stakers is then exactly 1% a year.
    private const ulong EpochDuration = 1800;
    private const ulong StakeForOnePercentRoi = 4_380_000_000;

    private IBlockTree _blockTree = null!;
    private ISpecProvider _specProvider = null!;
    private IEpochSwitchManager _epochSwitchManager = null!;
    private ISigningTxCache _signingTxCache = null!;
    private IMasternodeVotingContract _votingContract = null!;
    private IMintedRecordContract _mintedRecordContract = null!;
    private IRewardsStore _rewardsStore = null!;
    private IReadOnlyTxProcessingEnvFactory _txProcessingEnvFactory = null!;
    private ITransactionProcessor _transactionProcessor = null!;
    private IWorldState _worldState = null!;
    private IXdcReleaseSpec _spec = null!;
    private XdcMasternodeEthModule _module = null!;

    [SetUp]
    public void Setup()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _specProvider = Substitute.For<ISpecProvider>();
        _epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        _signingTxCache = Substitute.For<ISigningTxCache>();
        _votingContract = Substitute.For<IMasternodeVotingContract>();
        _mintedRecordContract = Substitute.For<IMintedRecordContract>();
        _rewardsStore = Substitute.For<IRewardsStore>();
        _worldState = Substitute.For<IWorldState>();
        _transactionProcessor = Substitute.For<ITransactionProcessor>();
        _txProcessingEnvFactory = CreateProcessingEnvFactory(_worldState, _transactionProcessor);

        _spec = XdcTestHelper.CreateXdcReleaseSpec(epochLength: EpochLength, maxMasternodes: 3);
        _spec.MergeSignRange = MergeSignRange;
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(_spec);

        _module = new XdcMasternodeEthModule(
            _blockTree,
            _specProvider,
            _epochSwitchManager,
            _signingTxCache,
            _votingContract,
            _mintedRecordContract,
            _rewardsStore,
            _txProcessingEnvFactory);
    }

    [Test]
    public void eth_getBlockSignersByNumber_collects_masternodes_that_signed_the_representative_block()
    {
        // Block 800 is represented by block 810, the next multiple of MergeSignRange.
        XdcBlockHeader queried = AddCanonicalHeader(800);
        XdcBlockHeader signed = AddCanonicalHeader(810);
        SetHead();
        SetMasternodes(signed, TestItem.AddressA, TestItem.AddressB, TestItem.AddressC);
        AddSigningBlock(811, signed.Hash!, TestItem.AddressA, TestItem.AddressC);

        ResultWrapper<Address[]> result = _module.eth_getBlockSignersByNumber(new BlockParameter(queried.Number));

        Assert.That(result.Data, Is.EquivalentTo(new[] { TestItem.AddressA, TestItem.AddressC }));
    }

    [Test]
    public void eth_getBlockSignersByHash_ignores_signatures_for_other_blocks()
    {
        XdcBlockHeader signed = AddCanonicalHeader(810);
        SetHead();
        SetMasternodes(signed, TestItem.AddressA);
        AddSigningBlock(811, TestItem.KeccakF, TestItem.AddressA);

        ResultWrapper<Address[]> result = _module.eth_getBlockSignersByHash(signed.Hash!);

        Assert.That(result.Data, Is.Empty);
    }

    [Test]
    public void eth_getBlockSignersByHash_returns_empty_for_an_unknown_block()
    {
        ResultWrapper<Address[]> result = _module.eth_getBlockSignersByHash(TestItem.KeccakF);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Is.Empty);
        }
    }

    [Test]
    public void eth_getBlockSignersByNumber_reports_no_signers_when_the_chainspec_defines_no_signing_schedule()
    {
        XdcBlockHeader queried = AddCanonicalHeader(800);
        SetHead();
        _spec.MergeSignRange = 0;

        ResultWrapper<Address[]> result = _module.eth_getBlockSignersByNumber(new BlockParameter(queried.Number));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Is.Empty);
        }
    }

    [TestCase(true, 50u, TestName = "Canonical block reports the share of masternodes that signed it")]
    [TestCase(false, 0u, TestName = "Block off the canonical chain never gains finality")]
    public void eth_getBlockFinalityByNumber_reports_the_signed_share_of_the_canonical_chain(bool isMainChain, uint expected)
    {
        XdcBlockHeader queried = AddCanonicalHeader(800);
        XdcBlockHeader signed = AddCanonicalHeader(810);
        SetHead();
        SetMasternodes(signed, TestItem.AddressA, TestItem.AddressB);
        AddSigningBlock(811, signed.Hash!, TestItem.AddressA);
        _blockTree.IsMainChain(queried.Hash!, false).Returns(isMainChain);

        ResultWrapper<uint> result = _module.eth_getBlockFinalityByNumber(new BlockParameter(queried.Number));

        Assert.That(result.Data, Is.EqualTo(expected));
    }

    [Test]
    public void eth_getCandidates_reports_masternode_proposed_and_slashed_candidates()
    {
        XdcBlockHeader head = SetUpCheckpoint(
            masternodes: [TestItem.AddressA],
            penalties: [TestItem.AddressC]);
        SetCandidates(head, (TestItem.AddressA, 300), (TestItem.AddressB, 200), (TestItem.AddressC, 100));

        ResultWrapper<XdcCandidatesResult> result = _module.eth_getCandidates();

        Dictionary<string, XdcCandidateInfo> candidates = result.Data.Candidates!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Success, Is.True);
            Assert.That(result.Data.Epoch, Is.EqualTo((long)CurrentEpoch));
            Assert.That(candidates[TestItem.AddressA.ToString()].Status, Is.EqualTo("MASTERNODE"));
            Assert.That(candidates[TestItem.AddressA.ToString()].Capacity, Is.EqualTo(new BigInteger(300)));
            Assert.That(candidates[TestItem.AddressB.ToString()].Status, Is.EqualTo("PROPOSED"));
            Assert.That(candidates[TestItem.AddressC.ToString()].Status, Is.EqualTo("SLASHED"));
        }
    }

    [Test]
    public void eth_getCandidates_reports_a_masternode_that_is_no_longer_a_candidate_with_an_unknown_stake()
    {
        XdcBlockHeader head = SetUpCheckpoint(masternodes: [TestItem.AddressD], penalties: []);
        SetCandidates(head, (TestItem.AddressA, 300));

        ResultWrapper<XdcCandidatesResult> result = _module.eth_getCandidates();

        XdcCandidateInfo info = result.Data.Candidates![TestItem.AddressD.ToString()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(info.Status, Is.EqualTo("MASTERNODE"));
            Assert.That(info.Capacity, Is.EqualTo(BigInteger.MinusOne));
        }
    }

    [Test]
    public void eth_getCandidates_reads_an_explicit_epoch_from_the_checkpoint_rather_than_the_head()
    {
        SetUpCheckpoint(masternodes: [TestItem.AddressA], penalties: []);
        SetCandidates(CheckpointOf(CurrentEpoch), (TestItem.AddressA, 300), (TestItem.AddressB, 200));

        ResultWrapper<XdcCandidatesResult> result = _module.eth_getCandidates(ParseEpoch(CurrentEpoch.ToString()));

        Assert.That(result.Data.Candidates, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task eth_getCandidates_serializes_capacity_as_a_decimal_string()
    {
        XdcBlockHeader head = SetUpCheckpoint(masternodes: [TestItem.AddressD], penalties: []);
        SetCandidates(head, (TestItem.AddressA, 300));

        string json = await RpcTest.TestSerializedRequest<IXdcMasternodeEthRpcModule>(_module, "eth_getCandidates");

        // BigInteger keeps the reference's -1 "no stake to read" sentinel; Nethermind's converter quotes it.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Contain("\"capacity\":\"300\""));
            Assert.That(json, Does.Contain("\"capacity\":\"-1\""));
        }
    }

    [Test]
    public void eth_getCandidates_fails_for_an_epoch_before_the_v2_switch()
    {
        SetUpCheckpoint(masternodes: [TestItem.AddressA], penalties: []);
        _spec.SwitchEpoch = 5;

        ResultWrapper<XdcCandidatesResult> result = _module.eth_getCandidates(ParseEpoch("2"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.Result.Error, Does.Contain("V1 epoch"));
        }
    }

    [TestCaseSource(nameof(CandidateStatusCases))]
    public (string Status, BigInteger Capacity) eth_getCandidateStatus_reports_the_status_of_one_address(Address coinbase)
    {
        XdcBlockHeader head = SetUpCheckpoint(
            masternodes: [TestItem.AddressA],
            penalties: [TestItem.AddressC]);
        SetCandidates(head, (TestItem.AddressA, 300), (TestItem.AddressB, 200), (TestItem.AddressC, 100));

        ResultWrapper<XdcCandidateStatusResult> result = _module.eth_getCandidateStatus(coinbase);

        Assert.That(result.Data.Success, Is.True);
        return (result.Data.Status, result.Data.Capacity);
    }

    private static IEnumerable<TestCaseData> CandidateStatusCases()
    {
        yield return new TestCaseData(TestItem.AddressA)
            .Returns(("MASTERNODE", new BigInteger(300))).SetName("Masternode that is still a candidate keeps its stake");
        yield return new TestCaseData(TestItem.AddressB)
            .Returns(("PROPOSED", new BigInteger(200))).SetName("Candidate outside the masternode set is proposed");
        yield return new TestCaseData(TestItem.AddressC)
            .Returns(("SLASHED", new BigInteger(100))).SetName("Penalized candidate is slashed");
        yield return new TestCaseData(TestItem.AddressD)
            .Returns((string.Empty, BigInteger.Zero)).SetName("Unknown address has no status");
    }

    [Test]
    public void eth_getTokenStats_sums_pre_and_post_upgrade_supply()
    {
        SetUpCheckpoint(masternodes: [TestItem.AddressA], penalties: []);
        BlockHeader rewardBlock = AddCanonicalHeader(950);
        SetOnsetEpoch(3);
        _mintedRecordContract.GetEpochAccounting(_worldState, (UInt256)CurrentEpoch)
            .Returns(new MintedRecordAccounting(700, 25, 950));

        ResultWrapper<XdcTokenSupply> result = _module.eth_getTokenStats();

        // Two epochs ran before the upgrade at epoch 3, each minting the flat 5000 XDC reward.
        UInt256 preUpgradeMinted = (UInt256)2 * 5000 * Unit.Ether;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.V1!.Minted, Is.EqualTo(preUpgradeMinted));
            Assert.That(result.Data.V2!.Minted, Is.EqualTo((UInt256)700));
            Assert.That(result.Data.V2.Burned, Is.EqualTo((UInt256)25));
            Assert.That(result.Data.Minted, Is.EqualTo(preUpgradeMinted + 700));
            Assert.That(result.Data.UpgradeEpochNum, Is.EqualTo((UInt256)3));
            Assert.That(result.Data.EpochNum, Is.EqualTo((UInt256)CurrentEpoch));
            Assert.That(result.Data.BlockNumber, Is.EqualTo((UInt256)950));
            Assert.That(result.Data.BlockHash, Is.EqualTo(rewardBlock.Hash));
        }
    }

    [Test]
    public void eth_getTokenStats_fails_when_the_reward_upgrade_has_not_been_applied()
    {
        SetUpCheckpoint(masternodes: [TestItem.AddressA], penalties: []);
        _mintedRecordContract.TryGetOnsetEpoch(_worldState, out Arg.Any<UInt256>()).Returns(false);

        ResultWrapper<XdcTokenSupply> result = _module.eth_getTokenStats();

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public void eth_getTokenStats_rejects_an_epoch_after_the_current_one()
    {
        SetUpCheckpoint(masternodes: [TestItem.AddressA], penalties: []);
        SetOnsetEpoch(1);

        ResultWrapper<XdcTokenSupply> result = _module.eth_getTokenStats(ParseEpoch("99"));

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public void eth_getStakerROI_annualizes_the_staker_share_of_one_epoch_reward()
    {
        XdcBlockHeader rewarded = SetUpEpochTimeline();
        SetCandidates(rewarded, (TestItem.AddressA, (UInt256)StakeForOnePercentRoi * Unit.Ether));

        ResultWrapper<double> result = _module.eth_getStakerROI();

        Assert.That(result.Data, Is.EqualTo(1.0).Within(1e-9));
    }

    [Test]
    public void eth_getStakerROI_returns_zero_when_nothing_is_staked()
    {
        XdcBlockHeader rewarded = SetUpEpochTimeline();
        SetCandidates(rewarded);

        ResultWrapper<double> result = _module.eth_getStakerROI();

        Assert.That(result.Data, Is.Zero);
    }

    [Test]
    public void eth_getStakerROIMasternode_annualizes_the_rewards_paid_out_for_one_masternode()
    {
        SetUpEpochTimeline();
        XdcBlockHeader current = CheckpointOf(RoiCurrentEpoch);
        XdcEpochRewards rewards = new()
        {
            Rewards = new()
            {
                [TestItem.AddressA.ToString()] = new()
                {
                    [TestItem.AddressB.ToString()] = ((UInt256)4_500 * Unit.Ether).ToString(),
                    [TestItem.AddressC.ToString()] = ((UInt256)500 * Unit.Ether).ToString(),
                },
            },
        };
        _rewardsStore.TryGetEpochRewards(Arg.Any<Hash256>(), out Arg.Any<XdcEpochRewards?>())
            .Returns(x => { x[1] = rewards; return true; });
        _votingContract.GetVoters(_transactionProcessor, current, TestItem.AddressA).Returns([TestItem.AddressB]);
        _votingContract.GetVoterStake(_transactionProcessor, current, TestItem.AddressA, TestItem.AddressB)
            .Returns((UInt256)StakeForOnePercentRoi * Unit.Ether);

        ResultWrapper<double> result = _module.eth_getStakerROIMasternode(TestItem.AddressA);

        Assert.That(result.Data, Is.EqualTo(1.0).Within(1e-9));
    }

    [Test]
    public void eth_getStakerROIMasternode_returns_zero_when_the_masternode_earned_nothing()
    {
        SetUpEpochTimeline();
        _rewardsStore.TryGetEpochRewards(Arg.Any<Hash256>(), out Arg.Any<XdcEpochRewards?>()).Returns(false);

        ResultWrapper<double> result = _module.eth_getStakerROIMasternode(TestItem.AddressA);

        Assert.That(result.Data, Is.Zero);
    }

    [TestCase("\"latest\"", null, TestName = "The latest keyword leaves the epoch unresolved")]
    [TestCase("\"12\"", 12ul, TestName = "A decimal string is an epoch number")]
    [TestCase("\"0x1f\"", 31ul, TestName = "A hex string is an epoch number")]
    [TestCase("12", 12ul, TestName = "A JSON number is an epoch number")]
    [TestCase("-1", null, TestName = "The rpc.EpochNumber latest sentinel is read as latest")]
    [TestCase("\"-1\"", null, TestName = "The latest sentinel is read as latest when quoted")]
    public void XdcEpochParameter_parses_the_reference_epoch_encodings(string json, ulong? expected) =>
        Assert.That(ParseEpochJson(json).EpochNumber, Is.EqualTo(expected));

    private static XdcEpochParameter ParseEpoch(string value) => ParseEpochJson($"\"{value}\"");

    private static XdcEpochParameter ParseEpochJson(string json)
    {
        XdcEpochParameter epoch = new();
        epoch.ReadJson(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions());
        return epoch;
    }

    private static IReadOnlyTxProcessingEnvFactory CreateProcessingEnvFactory(IWorldState worldState, ITransactionProcessor transactionProcessor)
    {
        IReadOnlyTxProcessingScope scope = Substitute.For<IReadOnlyTxProcessingScope>();
        scope.WorldState.Returns(worldState);
        scope.TransactionProcessor.Returns(transactionProcessor);
        IReadOnlyTxProcessorSource source = Substitute.For<IReadOnlyTxProcessorSource>();
        source.Build(Arg.Any<BlockHeader>()).Returns(scope);
        IReadOnlyTxProcessingEnvFactory factory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        factory.Create().Returns(source);
        return factory;
    }

    private XdcBlockHeader CheckpointOf(ulong epochNumber)
    {
        BlockRoundInfo info = _epochSwitchManager.GetBlockByEpochNumber(epochNumber)!;
        return (XdcBlockHeader)_blockTree.FindHeader(info.Hash, info.BlockNumber)!;
    }

    private void SetOnsetEpoch(ulong onsetEpoch) =>
        _mintedRecordContract.TryGetOnsetEpoch(_worldState, out Arg.Any<UInt256>())
            .Returns(x => { x[1] = (UInt256)onsetEpoch; return true; });

    private XdcBlockHeader AddCanonicalHeader(ulong number, ulong timestamp = 0)
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithNumber(number)
            .WithTimestamp(timestamp)
            .WithHash(Keccak.Compute($"block-{number}"))
            .WithExtraConsensusData(new ExtraFieldsV2(number, null!))
            .TestObject;
        _blockTree.FindHeader(number).Returns(header);
        _blockTree.FindHeader(header.Hash!).Returns(header);
        _blockTree.FindHeader(header.Hash!, number).Returns(header);
        _blockTree.FindHeader(Arg.Is<BlockParameter>(p => p.BlockNumber == number), Arg.Any<bool>()).Returns(header);
        return header;
    }

    private void SetHead(ulong number = HeadNumber)
    {
        Block head = new(AddCanonicalHeader(number));
        _blockTree.Head.Returns(head);
    }

    private void SetMasternodes(XdcBlockHeader header, params Address[] masternodes) =>
        _epochSwitchManager.GetEpochSwitchInfo(header)
            .Returns(new EpochSwitchInfo(masternodes, [], [], new BlockRoundInfo(header.Hash!, header.Number, header.Number)));

    private void AddSigningBlock(ulong number, Hash256 signedBlockHash, params Address[] signers)
    {
        XdcBlockHeader header = AddCanonicalHeader(number);
        _blockTree.FindBlock(number, Arg.Any<BlockTreeLookupOptions>()).Returns(new Block(header));

        Transaction[] signingTxs = new Transaction[signers.Length];
        for (int i = 0; i < signers.Length; i++)
        {
            signingTxs[i] = SignTransactionManager.CreateTxSign(
                (UInt256)number, signedBlockHash, 0, _spec.BlockSignerContract, signers[i]);
        }

        _signingTxCache.GetSigningTransactions(header.Hash!, number, _spec).Returns(signingTxs);
    }

    private XdcBlockHeader AddCheckpoint(ulong epochNumber, ulong blockNumber, ulong timestamp, Address[]? masternodes = null, Address[]? penalties = null)
    {
        XdcBlockHeader checkpoint = Build.A.XdcBlockHeader()
            .WithNumber(blockNumber)
            .WithTimestamp(timestamp)
            .WithHash(Keccak.Compute($"block-{blockNumber}"))
            .WithValidators(masternodes ?? [])
            .WithPenalties(penalties ?? [])
            .WithExtraConsensusData(new ExtraFieldsV2(blockNumber, null!))
            .TestObject;
        _blockTree.FindHeader(checkpoint.Hash!, blockNumber).Returns(checkpoint);
        _epochSwitchManager.GetBlockByEpochNumber(epochNumber)
            .Returns(new BlockRoundInfo(checkpoint.Hash!, blockNumber, blockNumber));
        return checkpoint;
    }

    /// <summary>Puts the head in epoch <see cref="CurrentEpoch"/> with its checkpoint at <see cref="CheckpointNumber"/>.</summary>
    /// <returns>The head, whose state the candidate list of the latest epoch is read from.</returns>
    private XdcBlockHeader SetUpCheckpoint(Address[] masternodes, Address[] penalties)
    {
        SetHead();
        AddCheckpoint(CurrentEpoch, CheckpointNumber, 0, masternodes, penalties);
        return (XdcBlockHeader)_blockTree.Head!.Header;
    }

    // A head at round 2000 sits in epoch 2, so the last fully rewarded epoch is epoch 0.
    private const ulong RoiHeadNumber = 2000;
    private const ulong RoiCurrentEpoch = 2;

    /// <summary>Lays out the three most recent epoch checkpoints <see cref="EpochDuration"/> apart.</summary>
    /// <returns>The checkpoint of the last settled epoch, which is where the reference reads staked totals.</returns>
    private XdcBlockHeader SetUpEpochTimeline()
    {
        SetHead(RoiHeadNumber);
        AddCheckpoint(RoiCurrentEpoch, 1800, 2 * EpochDuration);
        AddCheckpoint(RoiCurrentEpoch - 2, 1, 0);
        return AddCheckpoint(RoiCurrentEpoch - 1, 900, EpochDuration);
    }

    private void SetCandidates(BlockHeader stateHeader, params (Address Address, UInt256 Stake)[] candidates)
    {
        Address[] addresses = new Address[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            addresses[i] = candidates[i].Address;
            _votingContract.GetCandidateStake(_transactionProcessor, stateHeader, candidates[i].Address)
                .Returns(candidates[i].Stake);
        }

        _votingContract.GetCandidates(_transactionProcessor, stateHeader).Returns(addresses);
    }
}
