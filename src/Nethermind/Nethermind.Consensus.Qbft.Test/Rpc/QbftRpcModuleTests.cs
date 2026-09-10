// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Contracts;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.Rpc;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Rpc;

/// <summary>
/// Port of Besu's <c>qbft_*</c> JSON-RPC method tests (<c>QbftGetValidatorsByBlockNumberTest</c>, <c>QbftGetSignerMetricsTest</c>,
/// <c>QbftProposeValidatorVoteTest</c>, ...) plus coverage of the Nethermind-only methods.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class QbftRpcModuleTests
{
    private static readonly Address[] Validators = [QbftTestData.Addr(1), QbftTestData.Addr(2), QbftTestData.Addr(3)];
    private readonly List<PrivateKey> _keys = QbftTestData.Keys(3);
    private TestChain _chain = null!;

    [SetUp]
    public void Setup()
    {
        _chain = new TestChain();
        _chain.Add(Validators[0], Validators);
    }

    private IValidatorProvider BlockHeaderValidatorProvider() =>
        BlockValidatorProvider.NonForking(_chain.BlockTree, new EpochManager(30_000), QbftTestMessages.BlockInterface);

    private QbftRpcModule CreateModule(IValidatorProvider? validatorProvider = null, ISigner? signer = null, QbftChainSpecEngineParameters? parameters = null)
    {
        parameters ??= new QbftChainSpecEngineParameters { RequestTimeoutSeconds = 8, BlockPeriodSeconds = 4 };
        IValidatorProvider provider = validatorProvider ?? BlockHeaderValidatorProvider();
        return new QbftRpcModule(
            _chain.BlockTree,
            provider,
            Substitute.For<IValidatorContract>(),
            new EpochManager(parameters.EpochLength),
            QbftTestMessages.BlockInterface,
            QbftForksSchedule.Create(parameters, ulong.MaxValue),
            parameters,
            signer ?? NullSigner.Instance,
            new ValidatorPeers(provider, LimboLogs.Instance),
            new QbftConsensusStatus());
    }

    /// <summary>Blocks 1..count proposed round-robin by <see cref="Validators"/>.</summary>
    private void AddBlocks(int count)
    {
        for (int i = 1; i <= count; i++) _chain.Add(Validators[i % Validators.Length], Validators);
    }

    [Test]
    public void GetRequestTimeoutSecondsReturnsConfiguredValue() =>
        Assert.That(CreateModule().qbft_getRequestTimeoutSeconds().Data, Is.EqualTo(8));

    [Test]
    public void GetValidatorsByBlockHashReturnsValidatorsFromBlock()
    {
        AddBlocks(2);
        Assert.That(CreateModule().qbft_getValidatorsByBlockHash(_chain.Blocks[1].Hash!).Data, Is.EqualTo(Validators));
    }

    [Test]
    public void GetValidatorsByBlockHashReturnsNullForUnknownBlock() =>
        Assert.That(CreateModule().qbft_getValidatorsByBlockHash(Keccak.Compute("nope")).Data, Is.Null);

    [Test]
    public void GetValidatorsByBlockNumberReturnsValidatorsFromBlockLatestAndPending()
    {
        AddBlocks(2);
        // A vote reaching majority changes the set for the block after the head, which is what "pending" reports.
        Address newcomer = QbftTestData.Addr(4);
        _chain.Add(Validators[0], Validators, Vote.AuthVote(newcomer));
        _chain.Add(Validators[1], Validators, Vote.AuthVote(newcomer));
        QbftRpcModule module = CreateModule();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(module.qbft_getValidatorsByBlockNumber(new BlockParameter(1)).Data, Is.EqualTo(Validators));
            Assert.That(module.qbft_getValidatorsByBlockNumber(BlockParameter.Latest).Data, Is.EqualTo(Validators));
            Assert.That(module.qbft_getValidatorsByBlockNumber(BlockParameter.Pending).Data, Is.EqualTo(new[] { Validators[0], Validators[1], Validators[2], newcomer }));
            Assert.That(module.qbft_getValidatorsByBlockNumber(new BlockParameter(99)).Data, Is.Null);
        }
    }

    [Test]
    public void ProposeDiscardAndPendingVotes()
    {
        QbftRpcModule module = CreateModule();
        Address candidate = QbftTestData.Addr(4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(module.qbft_proposeValidatorVote(candidate, true).Data, Is.True);
            Assert.That(module.qbft_proposeValidatorVote(Validators[2], false).Data, Is.True);
            Assert.That(module.qbft_getPendingVotes().Data, Is.EqualTo(new Dictionary<Address, bool> { [candidate] = true, [Validators[2]] = false }));
            Assert.That(module.qbft_discardValidatorVote(candidate).Data, Is.True);
            Assert.That(module.qbft_getPendingVotes().Data, Is.EqualTo(new Dictionary<Address, bool> { [Validators[2]] = false }));
        }
    }

    [Test]
    public void VoteMethodsAreNotEnabledInContractMode()
    {
        IValidatorProvider contractMode = Substitute.For<IValidatorProvider>();
        contractMode.GetVoteProviderAtHead().Returns((IVoteProvider?)null);
        QbftRpcModule module = CreateModule(contractMode);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(module.qbft_proposeValidatorVote(QbftTestData.Addr(4), true).ErrorCode, Is.EqualTo(QbftRpcModule.MethodNotEnabledErrorCode));
            Assert.That(module.qbft_discardValidatorVote(QbftTestData.Addr(4)).ErrorCode, Is.EqualTo(QbftRpcModule.MethodNotEnabledErrorCode));
            Assert.That(module.qbft_getPendingVotes().ErrorCode, Is.EqualTo(QbftRpcModule.MethodNotEnabledErrorCode));
        }
    }

    [Test]
    public void GetSignerMetricsWhenNoParamsCoversTheLastHundredBlocksBelowHead()
    {
        AddBlocks(5);
        SignerMetricResult[] metrics = CreateModule().qbft_getSignerMetrics().Data;
        // Blocks 0..4: proposers 0,1,2,0,1 -> validator 0 twice (last at 3), 1 twice (last at 4), 2 once (at 2).
        Assert.That(metrics, Has.Length.EqualTo(3));
        Assert.That(Find(metrics, Validators[0]).ProposedBlockCount, Is.EqualTo((UInt256)2));
        Assert.That(Find(metrics, Validators[0]).LastProposedBlockNumber, Is.EqualTo((UInt256)3));
        Assert.That(Find(metrics, Validators[1]).LastProposedBlockNumber, Is.EqualTo((UInt256)4));
        Assert.That(Find(metrics, Validators[2]).ProposedBlockCount, Is.EqualTo((UInt256)1));
    }

    [Test]
    public void GetSignerMetricsListsValidatorsThatProposedNothingInRange()
    {
        AddBlocks(2);
        SignerMetricResult[] metrics = CreateModule().qbft_getSignerMetrics(new BlockParameter(1), new BlockParameter(2)).Data;
        // Only block 1 (proposer 1) is in [1, 2); validators 0 and 2 still appear with zero counts.
        Assert.That(metrics, Has.Length.EqualTo(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(metrics, Validators[1]).ProposedBlockCount, Is.EqualTo((UInt256)1));
            Assert.That(Find(metrics, Validators[0]).ProposedBlockCount, Is.EqualTo(UInt256.Zero));
            Assert.That(Find(metrics, Validators[2]).ProposedBlockCount, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void GetSignerMetricsWithEarliestAndLatest()
    {
        AddBlocks(3);
        QbftRpcModule module = CreateModule();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(module.qbft_getSignerMetrics(BlockParameter.Earliest, BlockParameter.Latest).Data, Has.Length.EqualTo(3));
            Assert.That(module.qbft_getSignerMetrics(new BlockParameter(1), BlockParameter.Pending).Data, Has.Length.EqualTo(3));
            Assert.That(module.qbft_getSignerMetrics(new BlockParameter(3), new BlockParameter(1)).ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        }
    }

    [Test]
    public void GetBlockCommittersRecoversSealsAndReportsVote()
    {
        Address[] validators = QbftTestMessages.Addresses(_keys);
        BftExtraData extraData = new(QbftTestData.ZeroVanity(), [], Vote.DropVote(validators[2]), 2, validators);
        // The commit digest ignores the seals, so it can be taken from the header before they are added.
        BlockHeader unsealed = QbftTestData.BftHeader(1, validators[0], QbftExtraDataCodec.Instance.Encode(extraData)).WithTimestamp(0).WithParentHash(_chain.Head.Hash!).TestObject;
        ValueHash256 digest = BftBlockHashing.CalculateCommitSealDigest(unsealed, QbftExtraDataCodec.Instance);
        EthereumEcdsa ecdsa = new(1);
        Signature[] seals = [ecdsa.Sign(_keys[1], in digest), ecdsa.Sign(_keys[2], in digest)];
        _chain.Add(validators[0], extraData.WithSeals(seals, 2));

        QbftBlockSealInfo? info = CreateModule().qbft_getBlockCommitters(new BlockParameter(1)).Data;
        Assert.That(info, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(info!.Proposer, Is.EqualTo(validators[0]));
            Assert.That(info.Round, Is.EqualTo(2));
            Assert.That(info.Committers, Is.EqualTo(new[] { validators[1], validators[2] }));
            Assert.That(info.Validators, Is.EqualTo(validators));
            Assert.That(info.VoteRecipient, Is.EqualTo(validators[2]));
            Assert.That(info.VoteIsAdd, Is.False);
            Assert.That(CreateModule().qbft_getBlockCommitters(new BlockParameter(7)).Data, Is.Null);
        }
    }

    [Test]
    public void GetConfigReportsTheForkActiveAtTheBlock()
    {
        AddBlocks(3);
        QbftChainSpecEngineParameters parameters = new()
        {
            BlockPeriodSeconds = 4,
            RequestTimeoutSeconds = 8,
            Transitions = [new QbftTransition { Block = 2, BlockPeriodSeconds = 10, BlockReward = 7, MiningBeneficiary = QbftTestData.Addr(9).ToString() }],
        };
        QbftRpcModule module = CreateModule(parameters: parameters);
        QbftConfigForRpc genesis = module.qbft_getConfig(new BlockParameter(1)).Data;
        QbftConfigForRpc latest = module.qbft_getConfig().Data;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(genesis.BlockPeriodSeconds, Is.EqualTo(4));
            Assert.That(genesis.ValidatorSelectionMode, Is.EqualTo("blockheader"));
            Assert.That(latest.BlockPeriodSeconds, Is.EqualTo(10));
            Assert.That(latest.BlockReward, Is.EqualTo((UInt256)7));
            Assert.That(latest.MiningBeneficiary, Is.EqualTo(QbftTestData.Addr(9)));
            Assert.That(module.qbft_getConfig(new BlockParameter(50)).ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
        }
    }

    [Test]
    public void GetNodeStatusDescribesTheLocalNode()
    {
        // The genesis (epoch block) validator list is the local node's key set.
        Address[] validators = QbftTestMessages.Addresses(_keys);
        System.Array.Sort(validators);
        _chain = new TestChain();
        _chain.Add(validators[0], validators);
        AddBlocks(3);
        QbftNodeStatus status = CreateModule(signer: new Signer(1, _keys[0], LimboLogs.Instance)).qbft_getNodeStatus().Data;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.LocalAddress, Is.EqualTo(_keys[0].Address));
            Assert.That(status.IsValidator, Is.True);
            Assert.That(status.CanSign, Is.True);
            Assert.That(status.ConsensusRunning, Is.False);
            Assert.That(status.ChainHeight, Is.EqualTo((UInt256)3));
            Assert.That(status.Validators, Is.EqualTo(validators));
            Assert.That(status.ConnectedValidators, Is.EqualTo(0));
            Assert.That(status.CurrentRound, Is.Null);
        }

        QbftNodeStatus observer = CreateModule().qbft_getNodeStatus().Data;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observer.IsValidator, Is.False);
            Assert.That(observer.CanSign, Is.False);
        }
    }

    private static SignerMetricResult Find(SignerMetricResult[] metrics, Address address)
    {
        foreach (SignerMetricResult metric in metrics)
        {
            if (metric.Address == address) return metric;
        }

        throw new AssertionException($"No metric for {address}");
    }
}
