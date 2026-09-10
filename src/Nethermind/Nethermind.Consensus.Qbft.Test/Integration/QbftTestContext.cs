// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Qbft.Validation;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Integration;

/// <summary>
/// Port of Besu's <c>TestContextBuilder</c>: an <c>n</c>-validator network in which the local node is the
/// real <see cref="QbftController"/> stack and every other validator is a <see cref="ValidatorPeer"/> that
/// injects signed messages and records what the local node multicasts.
/// </summary>
public sealed class QbftTestContextBuilder
{
    public const int EpochLength = 10_000;
    public const int BlockTimerSeconds = 3;
    public const int RoundTimerSeconds = 12;
    public const int MessageQueueLimit = 1000;
    public const int GossipedHistoryLimit = 100;
    public const int DuplicateMessageLimit = 100;
    public const int FutureMessagesMaxDistance = 10;
    public const int FutureMessagesLimit = 1000;

    private int _validatorCount = 4;
    private int _indexOfFirstLocallyProposedBlock;
    private bool _useGossip;
    private bool _earlyRoundChange;
    private ITimestamper _clock = new ManualTimestamper(DateTime.UnixEpoch);

    public QbftTestContextBuilder ValidatorCount(int count)
    {
        _validatorCount = count;
        return this;
    }

    /// <summary>0 makes the local node the genesis coinbase, so a remote peer proposes block 1; 1 makes the local node propose block 1.</summary>
    public QbftTestContextBuilder IndexOfFirstLocallyProposedBlock(int index)
    {
        _indexOfFirstLocallyProposedBlock = index;
        return this;
    }

    public QbftTestContextBuilder Clock(ITimestamper clock)
    {
        _clock = clock;
        return this;
    }

    public QbftTestContextBuilder UseGossip(bool useGossip)
    {
        _useGossip = useGossip;
        return this;
    }

    public QbftTestContextBuilder EarlyRoundChange(bool enabled)
    {
        _earlyRoundChange = enabled;
        return this;
    }

    public QbftTestContext BuildAndStart()
    {
        QbftTestContext context = Build();
        context.Controller.Start();
        return context;
    }

    public QbftTestContext Build()
    {
        List<PrivateKey> keys = QbftTestData.Keys(_validatorCount);
        keys.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        return new QbftTestContext(keys, _indexOfFirstLocallyProposedBlock, _clock, _useGossip, _earlyRoundChange);
    }
}

public sealed class QbftTestContext
{
    private readonly List<Block> _chain = [];
    private readonly Dictionary<Address, ValidatorPeer> _remotePeers = [];
    private readonly Address[] _validators;
    private readonly StubValidatorMulticaster _multicaster;

    public QbftTestContext(List<PrivateKey> sortedKeys, int localIndex, ITimestamper clock, bool useGossip, bool earlyRoundChange)
    {
        _validators = QbftTestMessages.Addresses(sortedKeys);
        LocalKey = sortedKeys[localIndex];
        Clock = clock;
        Codec = QbftTestMessages.Codec;
        BlockInterface = QbftTestMessages.BlockInterface;
        _chain.Add(CreateGenesis(_validators));

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(_ => _chain[^1]);
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(ci => FindHeader(ci.Arg<Hash256>()));
        blockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(ci => FindHeader(ci.Arg<ulong>()));
        BlockTree = blockTree;

        _multicaster = new StubValidatorMulticaster(LocalKey.Address);
        foreach (PrivateKey key in sortedKeys)
        {
            if (key != LocalKey)
            {
                ValidatorPeer peer = new(key, this);
                _remotePeers[key.Address] = peer;
                _multicaster.Peers.Add(peer);
            }
        }

        EpochManager epochManager = new(QbftTestContextBuilder.EpochLength);
        IValidatorProvider validatorProvider = BlockValidatorProvider.NonForking(blockTree, epochManager, BlockInterface);
        ProposerSelector = new BftProposerSelector(blockTree, validatorProvider);
        QbftForksSchedule forksSchedule = QbftForksSchedule.Create(new QbftChainSpecEngineParameters
        {
            EpochLength = QbftTestContextBuilder.EpochLength,
            BlockPeriodSeconds = QbftTestContextBuilder.BlockTimerSeconds,
            RequestTimeoutSeconds = QbftTestContextBuilder.RoundTimerSeconds,
        }, ulong.MaxValue);

        IBftEventQueue eventQueue = Substitute.For<IBftEventQueue>();
        RoundTimer roundTimer = new(eventQueue, new BftRoundExpiryTimeCalculator(TimeSpan.FromSeconds(QbftTestContextBuilder.RoundTimerSeconds)), Scheduler, LimboLogs.Instance);
        BlockTimer blockTimer = new(eventQueue, forksSchedule, Scheduler, clock, LimboLogs.Instance);

        LocalSigner = new Signer(1, LocalKey, LimboLogs.Instance);
        LocalMessageFactory = new MessageFactory(LocalSigner, Codec, BlockInterface);
        FinalState = new QbftFinalState(validatorProvider, LocalKey.Address, ProposerSelector, _multicaster, roundTimer, blockTimer, new HarnessBlockCreatorFactory(this), clock);

        IQbftBlockValidator blockValidator = Substitute.For<IQbftBlockValidator>();
        blockValidator.ValidateBlock(Arg.Any<Block>(), Arg.Any<ReadOnlyBlockAccessList?>()).Returns(BlockValidationResult.Valid);
        MessageValidatorFactory messageValidatorFactory = new(ProposerSelector, blockValidator, validatorProvider, BlockInterface, LimboLogs.Instance);
        QbftMessageTransmitter transmitter = new(LocalMessageFactory, Codec, _multicaster, LimboLogs.Instance);
        QbftRoundFactory roundFactory = new(FinalState, BlockInterface, new HarnessBlockImporter(this), [], messageValidatorFactory, LocalMessageFactory, transmitter, LimboLogs.Instance);
        QbftBlockHeightManagerFactory heightManagerFactory = new(
            FinalState, roundFactory, messageValidatorFactory, LocalMessageFactory, transmitter, validatorProvider, BlockInterface,
            new ValidatorModeTransitionLogger(forksSchedule, LimboLogs.Instance), LimboLogs.Instance)
        { IsEarlyRoundChangeEnabled = earlyRoundChange };

        IQbftGossiper gossiper = useGossip
            ? new QbftGossiper(new UniqueMessageMulticaster(_multicaster, QbftTestContextBuilder.GossipedHistoryLimit))
            : Substitute.For<IQbftGossiper>();
        Controller = new QbftController(
            blockTree,
            FinalState,
            heightManagerFactory,
            gossiper,
            new MessageTracker(QbftTestContextBuilder.DuplicateMessageLimit),
            new FutureMessageBuffer(QbftTestContextBuilder.FutureMessagesMaxDistance, QbftTestContextBuilder.FutureMessagesLimit, 0),
            Codec,
            LimboLogs.Instance);
    }

    public PrivateKey LocalKey { get; }
    public Address LocalAddress => LocalKey.Address;
    public Signer LocalSigner { get; }
    public MessageFactory LocalMessageFactory { get; }
    public QbftController Controller { get; }
    public QbftFinalState FinalState { get; }
    public BftProposerSelector ProposerSelector { get; }
    public QbftMessageCodec Codec { get; }
    public QbftBlockInterface BlockInterface { get; }
    public IBlockTree BlockTree { get; }
    public ITimestamper Clock { get; }
    public RecordingScheduler Scheduler { get; } = new();
    public long CurrentChainHeight => (long)_chain[^1].Number;
    public BlockHeader ChainHeadHeader => _chain[^1].Header;
    public IReadOnlyList<Address> Validators => _validators;

    public Block CreateBlockForProposalFromChainHead(ulong timestamp, int round = 0) => CreateBlockForProposal(ChainHeadHeader, timestamp, LocalAddress, round);

    public Block CreateBlockForProposalFromChainHead(ulong timestamp, Address proposer, int round = 0) => CreateBlockForProposal(ChainHeadHeader, timestamp, proposer, round);

    /// <summary>Deterministic empty block: the harness block creator and the tests build identical blocks from identical inputs.</summary>
    public Block CreateBlockForProposal(BlockHeader parent, ulong timestamp, Address proposer, int round)
    {
        byte[] extraData = QbftExtraDataCodec.Instance.Encode(new BftExtraData(QbftTestData.ZeroVanity(), [], null, round, _validators));
        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .WithTimestamp(timestamp)
            .WithBeneficiary(proposer)
            .WithDifficulty(UInt256.One)
            .WithMixHash(BftHelpers.ExpectedMixHash)
            .WithNonce(0)
            .WithGasLimit(5000)
            .WithGasUsed(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(extraData)
            .TestObject;
        return Build.A.Block.WithHeader(QbftTestData.ToQbftHeader(header)).TestObject;
    }

    public Block CreateSealedBlock(Block block, int round, IReadOnlyList<Signature> seals) => BlockInterface.CreateSealedBlock(block, round, seals);

    public Hash256 Digest(Block block) => new(BlockInterface.Digest(block));

    public Commit CreateLocalCommit(ConsensusRoundIdentifier round, Block block) =>
        LocalMessageFactory.CreateCommit(round, Digest(block), LocalMessageFactory.CreateCommitSeal(block, round.Round));

    /// <summary>A prepared certificate carrying prepares from every remote peer for <paramref name="block"/> in <paramref name="preparedRound"/>.</summary>
    public PreparedCertificate CreateValidPreparedCertificate(ConsensusRoundIdentifier preparedRound, Block block) =>
        new(block, RoundSpecificPeers(preparedRound).CreateSignedPreparePayloadOfAllPeers(preparedRound, Digest(block)), preparedRound.Round);

    public void AppendBlock(Block block)
    {
        if (block.Number != _chain[^1].Number + 1) throw new InvalidOperationException($"Block {block.Number} does not extend head {_chain[^1].Number}.");
        _chain.Add(block);
    }

    public void HandleNewChainHead() => Controller.HandleNewBlockEvent(new NewChainHeadEvent(ChainHeadHeader));

    public RoundSpecificPeers RoundSpecificPeers(ConsensusRoundIdentifier round)
    {
        Address proposerAddress = ProposerSelector.SelectProposerForRound(round);
        _remotePeers.TryGetValue(proposerAddress, out ValidatorPeer? proposer);
        List<ValidatorPeer> nonProposing = [];
        foreach (ValidatorPeer peer in _multicaster.Peers)
        {
            if (peer != proposer) nonProposing.Add(peer);
        }

        return new RoundSpecificPeers(proposer, _multicaster.Peers, nonProposing, this);
    }

    /// <summary>Delivers raw bytes to the local node as if received from <paramref name="sender"/>.</summary>
    public void Inject(Address sender, int code, byte[] data) =>
        Controller.HandleMessageEvent(new ReceivedMessageEvent(new QbftReceivedMessage(code, data, sender)));

    private static Block CreateGenesis(Address[] validators)
    {
        byte[] extraData = QbftExtraDataCodec.Instance.Encode(new BftExtraData(QbftTestData.ZeroVanity(), [], null, 0, validators));
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(0)
            .WithParentHash(Keccak.Zero)
            .WithTimestamp(0)
            .WithBeneficiary(validators[0])
            .WithDifficulty(UInt256.One)
            .WithMixHash(BftHelpers.ExpectedMixHash)
            .WithNonce(0)
            .WithGasLimit(5000)
            .WithGasUsed(0)
            .WithUnclesHash(Keccak.OfAnEmptySequenceRlp)
            .WithExtraData(extraData)
            .TestObject;
        return Build.A.Block.WithHeader(QbftTestData.ToQbftHeader(header)).TestObject;
    }

    private BlockHeader? FindHeader(Hash256 hash)
    {
        foreach (Block block in _chain)
        {
            if (block.Hash == hash) return block.Header;
        }

        return null;
    }

    private BlockHeader? FindHeader(ulong number) => number < (ulong)_chain.Count ? _chain[(int)number].Header : null;

    private sealed class HarnessBlockCreatorFactory(QbftTestContext context) : IQbftBlockCreatorFactory
    {
        public IQbftBlockCreator Create(int roundNumber) => new HarnessBlockCreator(context, roundNumber);
    }

    private sealed class HarnessBlockCreator(QbftTestContext context, int round) : IQbftBlockCreator
    {
        public BlockCreationResult CreateBlock(ulong headerTimestampSeconds, BlockHeader parentHeader) =>
            new(context.CreateBlockForProposal(parentHeader, headerTimestampSeconds, context.LocalAddress, round), null);
    }

    private sealed class HarnessBlockImporter(QbftTestContext context) : IQbftBlockImporter
    {
        public bool ImportBlock(Block block, ReadOnlyBlockAccessList? blockAccessList)
        {
            if (block.Number == context._chain[^1].Number + 1)
            {
                context._chain.Add(block);
            }

            return true;
        }
    }
}

/// <summary>Timer scheduler that never fires; tests drive expiries through the controller directly.</summary>
public sealed class RecordingScheduler : IBftTimerScheduler
{
    public List<(Action Callback, TimeSpan Delay)> Scheduled { get; } = [];

    public IDisposable Schedule(Action callback, TimeSpan delay)
    {
        Scheduled.Add((callback, delay));
        return Substitute.For<IDisposable>();
    }
}

/// <summary>Delivers the local node's multicasts to every remote peer not on the denylist.</summary>
public sealed class StubValidatorMulticaster(Address localAddress) : IValidatorMulticaster
{
    public List<ValidatorPeer> Peers { get; } = [];

    public void Send(int code, byte[] data) => Send(code, data, []);

    public void Send(int code, byte[] data, IReadOnlyCollection<Address> denylist)
    {
        foreach (ValidatorPeer peer in Peers)
        {
            if (!denylist.ContainsAddress(peer.Address))
            {
                peer.ReceivedMessages.Add(new QbftReceivedMessage(code, data, localAddress));
            }
        }
    }
}

/// <summary>A remote validator: signs messages with its own key and injects them into the local node.</summary>
public sealed class ValidatorPeer
{
    private readonly QbftTestContext _context;
    private readonly Signer _signer;

    public ValidatorPeer(PrivateKey key, QbftTestContext context)
    {
        _context = context;
        Key = key;
        _signer = new Signer(1, key, LimboLogs.Instance);
        MessageFactory = new MessageFactory(_signer, context.Codec, context.BlockInterface);
    }

    public PrivateKey Key { get; }
    public Address Address => Key.Address;
    public MessageFactory MessageFactory { get; }
    public List<QbftReceivedMessage> ReceivedMessages { get; } = [];

    public void ClearReceivedMessages() => ReceivedMessages.Clear();

    public Signature Sign(Hash256 digest)
    {
        ValueHash256 message = digest.ValueHash256;
        return _signer.TrySign(in message, out Signature? signature) ? signature : throw new InvalidOperationException("Peer key cannot sign.");
    }

    public void InjectMessage(int code, byte[] data) => _context.Inject(Address, code, data);

    public void InjectMessage(BftMessage message) => InjectMessage(message.MessageType, _context.Codec.Encode(message));

    public Proposal InjectProposal(ConsensusRoundIdentifier round, Block block) => InjectProposalForFutureRound(round, [], [], block);

    public Proposal InjectProposalForFutureRound(ConsensusRoundIdentifier round, IReadOnlyList<SignedData<RoundChangePayload>> roundChanges, IReadOnlyList<SignedData<PreparePayload>> prepares, Block block)
    {
        Proposal proposal = MessageFactory.CreateProposal(round, block, null, roundChanges, prepares);
        InjectMessage(proposal);
        return proposal;
    }

    public Prepare InjectPrepare(ConsensusRoundIdentifier round, Hash256 digest)
    {
        Prepare prepare = MessageFactory.CreatePrepare(round, digest);
        InjectMessage(prepare);
        return prepare;
    }

    public Commit InjectCommit(ConsensusRoundIdentifier round, Block block) =>
        InjectCommit(round, _context.Digest(block), MessageFactory.CreateCommitSeal(block, round.Round));

    public Commit InjectCommit(ConsensusRoundIdentifier round, Hash256 digest, Signature commitSeal)
    {
        Commit commit = MessageFactory.CreateCommit(round, digest, commitSeal);
        InjectMessage(commit);
        return commit;
    }

    public RoundChange InjectRoundChange(ConsensusRoundIdentifier round, PreparedCertificate? preparedCertificate)
    {
        RoundChange roundChange = MessageFactory.CreateRoundChange(round, preparedCertificate);
        InjectMessage(roundChange);
        return roundChange;
    }
}

/// <summary>The remote peers seen from one round: which of them proposes and which do not.</summary>
public sealed class RoundSpecificPeers(ValidatorPeer? proposer, IReadOnlyList<ValidatorPeer> peers, IReadOnlyList<ValidatorPeer> nonProposingPeers, QbftTestContext context)
{
    /// <summary>Null when the local node is the proposer for the round.</summary>
    public ValidatorPeer? Proposer => proposer;
    public ValidatorPeer RequiredProposer => proposer ?? throw new InvalidOperationException("The local node is the proposer for this round.");
    public ValidatorPeer FirstNonProposer => nonProposingPeers[0];
    public IReadOnlyList<ValidatorPeer> Peers => peers;

    public ValidatorPeer GetNonProposing(int index) => nonProposingPeers[index];

    public void ClearReceivedMessages()
    {
        foreach (ValidatorPeer peer in peers) peer.ClearReceivedMessages();
    }

    public Signature[] Sign(Hash256 digest)
    {
        Signature[] seals = new Signature[peers.Count];
        for (int i = 0; i < peers.Count; i++) seals[i] = peers[i].Sign(digest);
        return seals;
    }

    public List<SignedData<RoundChangePayload>> RoundChangeForNonProposing(ConsensusRoundIdentifier targetRound)
    {
        List<SignedData<RoundChangePayload>> result = [];
        foreach (ValidatorPeer peer in nonProposingPeers) result.Add(peer.InjectRoundChange(targetRound, null).SignedPayload);
        return result;
    }

    public void Commit(ConsensusRoundIdentifier round, Block block)
    {
        foreach (ValidatorPeer peer in peers) peer.InjectCommit(round, block);
    }

    public List<SignedData<RoundChangePayload>> RoundChange(ConsensusRoundIdentifier round)
    {
        List<SignedData<RoundChangePayload>> result = [];
        foreach (ValidatorPeer peer in peers) result.Add(peer.InjectRoundChange(round, null).SignedPayload);
        return result;
    }

    public List<SignedData<RoundChangePayload>> CreateSignedRoundChangePayload(ConsensusRoundIdentifier round, PreparedCertificate? preparedCertificate = null)
    {
        List<SignedData<RoundChangePayload>> result = [];
        foreach (ValidatorPeer peer in peers) result.Add(peer.MessageFactory.CreateRoundChange(round, preparedCertificate).SignedPayload);
        return result;
    }

    public void PrepareForNonProposing(ConsensusRoundIdentifier round, Hash256 digest)
    {
        foreach (ValidatorPeer peer in nonProposingPeers) peer.InjectPrepare(round, digest);
    }

    public void CommitForNonProposing(ConsensusRoundIdentifier round, Block block)
    {
        foreach (ValidatorPeer peer in nonProposingPeers) peer.InjectCommit(round, block);
    }

    public List<SignedData<PreparePayload>> CreateSignedPreparePayloadOfAllPeers(ConsensusRoundIdentifier preparedRound, Hash256 digest)
    {
        List<SignedData<PreparePayload>> result = [];
        foreach (ValidatorPeer peer in peers) result.Add(peer.MessageFactory.CreatePrepare(preparedRound, digest).SignedPayload);
        return result;
    }

    public void VerifyNoMessagesReceived() => VerifyMessagesReceived(peers);

    public void VerifyNoMessagesReceivedNonProposing() => VerifyMessagesReceived(nonProposingPeers);

    public void VerifyNoMessagesReceivedProposer() => VerifyMessagesReceived([RequiredProposer]);

    public void VerifyMessagesReceivedProposer(params BftMessage[] messages) => VerifyMessagesReceived([RequiredProposer], messages);

    public void VerifyMessagesReceivedNonProposing(params BftMessage[] messages) => VerifyMessagesReceived(nonProposingPeers, messages);

    public void VerifyMessagesReceivedNonProposingExcluding(ValidatorPeer exclude, params BftMessage[] messages)
    {
        List<ValidatorPeer> candidates = [];
        foreach (ValidatorPeer peer in nonProposingPeers)
        {
            if (peer != exclude) candidates.Add(peer);
        }

        VerifyMessagesReceived(candidates, messages);
    }

    public void VerifyMessagesReceived(params BftMessage[] messages) => VerifyMessagesReceived(peers, messages);

    /// <summary>Every candidate received exactly <paramref name="messages"/>, byte for byte and in order; then their inboxes are cleared.</summary>
    private void VerifyMessagesReceived(IReadOnlyList<ValidatorPeer> candidates, params BftMessage[] messages)
    {
        foreach (ValidatorPeer peer in candidates)
        {
            Assert.That(peer.ReceivedMessages, Has.Count.EqualTo(messages.Length), $"peer {peer.Address} received {Describe(peer.ReceivedMessages)}");
            for (int i = 0; i < messages.Length; i++)
            {
                QbftReceivedMessage actual = peer.ReceivedMessages[i];
                Assert.That(actual.Code, Is.EqualTo(messages[i].MessageType), $"message {i} type for peer {peer.Address}");
                Assert.That(actual.Data, Is.EqualTo(context.Codec.Encode(messages[i])), $"message {i} ({QbftMessageCode.Name(actual.Code)}) for peer {peer.Address}");
            }

            peer.ClearReceivedMessages();
        }
    }

    private static string Describe(List<QbftReceivedMessage> received)
    {
        List<string> names = [];
        foreach (QbftReceivedMessage message in received) names.Add(QbftMessageCode.Name(message.Code));
        return $"[{string.Join(", ", names)}]";
    }
}
