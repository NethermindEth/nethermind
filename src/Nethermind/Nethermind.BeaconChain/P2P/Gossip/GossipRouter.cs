// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Why a gossip message was dropped before reaching the typed events.</summary>
public enum GossipDropReason
{
    Oversized,
    InvalidSnappy,
    InvalidSsz,
    FutureSlot,
    StaleSlot,
    Duplicate,

    /// <summary>A body operation, execution request, withdrawal or blob count over its limit.</summary>
    LimitExceeded,

    /// <summary>A field that breaks a gossip rule needing no beacon state.</summary>
    InvalidField,

    /// <summary>A topic that is not an eth2 gossip topic this node handles under a current digest.</summary>
    UnknownTopic,

    /// <summary>A slot at or below the finalized checkpoint's start slot.</summary>
    BeforeFinalized,

    /// <summary>A message carrying a signature, which the eth2 <c>StrictNoSign</c> policy forbids.</summary>
    SignedMessage,

    /// <summary>A message whose stored block is not cached and would exceed the per-slot budget of store decodes.</summary>
    StoreDecodeBudgetSpent,

    /// <summary>A block whose slot is not higher than the slot of its held parent.</summary>
    NotAboveParentSlot,
}

/// <summary>
/// Subscribes the eth2 gossip topics for a fork digest and raises typed events for messages that
/// pass the gossip checks needing no beacon state.
/// </summary>
/// <remarks>
/// Validation here is intentionally limited to what needs no beacon state: snappy decompression
/// within the type's size bound, SSZ decoding as the topic fork's type, duplicate suppression, slot
/// sanity against the wall clock and the finalized checkpoint, and the stateless field and limit
/// rules of each topic. Proposer signature and shuffling, parent-block checks and aggregator
/// selection require the head state and belong to the orchestrator import pipeline consuming these
/// events. A block or aggregate for the next slot that arrives early is held and raised once its slot starts.
/// </remarks>
/// <param name="store">Where a block's parent and an execution payload envelope's block and bid are read from; <c>null</c> holds no block, so every envelope passing the stateless rules is consumed unchecked.</param>
/// <param name="status">Where the finalized checkpoint is read from; <c>null</c> applies no finalized-slot rule.</param>
public sealed class GossipRouter(BeaconChainSpec spec, SlotClock slotClock, ILogManager logManager, BeaconChainStore? store = null, IBeaconChainStatusSource? status = null)
{
    /// <summary>The spec <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c>.</summary>
    public const long MaximumGossipClockDisparityMs = 500;

    // gloas/p2p-interface.md type-specific SSZ bounds: the Gloas lists are progressive, so the types themselves carry none.
    private const int MaxSignedAggregateAndProofSizeGloas = 16829;
    private const int MaxAttesterSlashingSizeGloas = 2097616;

    // Fixed-size SSZ: validator_index (8), PayloadAttestationData (32 + 8 + 1 + 1) and the BLS signature (96).
    private const int PayloadAttestationMessageSize = 146;

    internal const int SeenCacheSize = 2048;
    private const int SeenProposalCacheSize = 1024;
    private const int SeenEnvelopeCacheSize = 1024;
    private const int EnvelopeBlockCacheSize = 1024;
    private const int SeenSlashedIndexCacheSize = 8192;
    private const int SeenPayloadAttestationCacheSize = 2048;
    private const int MaxDeferredMessages = 64;

    /// <summary>The most stored blocks decoded per slot for envelopes whose block is not cached and not canonical at a recent slot.</summary>
    /// <remarks>
    /// The pinned pubsub library has no peer scoring, so a REJECT costs its sender nothing and cannot bound decode work. An envelope
    /// naming the canonical block at its own slot, for the current or previous slot, is decoded outside the budget: that is the honest
    /// case, and the block is then cached. A root the store does not hold costs a key lookup, not a decode.
    /// </remarks>
    internal const int StoreDecodesPerSlot = 16;

    /// <summary>The most stored blocks decoded per slot to read the slot of a gossip block's held, uncached parent.</summary>
    internal const int ParentSlotReadsPerSlot = 16;

    /// <summary>The most stored blocks decoded per slot to read the slot of the held, uncached block a Gloas aggregate with a non-zero index votes for.</summary>
    internal const int VotedBlockSlotReadsPerSlot = 16;

    /// <summary>The most stored blocks decoded per slot to read the slot of the held, uncached block a payload attestation votes on.</summary>
    internal const int PtcBlockSlotReadsPerSlot = 16;

    private readonly ILogger _logger = logManager.GetClassLogger<GossipRouter>();
    private readonly LruKeyCache<ValueHash256> _seenMessages = new(SeenCacheSize, "beacon gossip seen messages");

    // Apart from _seenMessages, so a flood of cheap distinct votes cannot evict the message ids of the other topics.
    private readonly LruKeyCache<ValueHash256> _seenPayloadAttestationMessages = new(SeenCacheSize, "beacon gossip seen payload attestation messages");
    private readonly long[] _dropCounts = new long[Enum.GetValues<GossipDropReason>().Length];
    private readonly Lock _subscriptionLock = new();
    private readonly Dictionary<string, List<(ITopic Topic, Action<byte[]> Handler)>> _subscriptions = [];

    // Written by the import worker once a block's proposer signature verifies; read on the network thread.
    private readonly LruKeyCache<(ulong Slot, ulong Proposer)> _seenProposals = new(SeenProposalCacheSize, "beacon gossip proposals");

    // Written by the import worker once an envelope verifies; read on the network thread.
    private readonly LruKeyCache<(Hash256 Root, ulong BuilderIndex)> _seenEnvelopes = new(SeenEnvelopeCacheSize, "beacon gossip envelopes");

    // Written once an attester slashing verifies; read on the network thread. An evicted index only makes a later slashing consumed again.
    private readonly LruKeyCache<ulong> _seenSlashedIndices = new(SeenSlashedIndexCacheSize, "beacon gossip slashed indices");

    // Claimed when a payload attestation is raised, whatever its signature: fork choice pays a BLS verify for each raised vote, so a repeat with another signature must not reach it.
    private readonly LruKeyCache<(ulong Slot, ulong ValidatorIndex)> _seenPayloadAttestations = new(SeenPayloadAttestationCacheSize, "beacon gossip payload attestations");

    private readonly StoredBlockSlots? _storedBlockSlots = store is null ? null : new(store, slotClock, logManager.GetClassLogger<GossipRouter>(), "beacon gossip stored block slots");

    // One budget per rule, so messages naming held blocks under one rule cannot starve the store reads of another.
    private readonly PerSlotBudget _parentSlotReads = new(ParentSlotReadsPerSlot);
    private readonly PerSlotBudget _votedBlockSlotReads = new(VotedBlockSlotReadsPerSlot);
    private readonly PerSlotBudget _envelopeBlockDecodes = new(StoreDecodesPerSlot);
    private readonly PerSlotBudget _ptcBlockSlotReads = new(PtcBlockSlotReadsPerSlot);

    // A held block never changes, so its bid is decoded once, not once per envelope; a root not held is never cached, as it may arrive a moment later.
    private readonly LruCache<Hash256, EnvelopeBlock> _envelopeBlocks = new(EnvelopeBlockCacheSize, "beacon gossip envelope blocks");
    private readonly Lock _deferredLock = new();
    private readonly List<(ulong Slot, Action Raise)> _deferred = [];
    private int _deferredCount;

    private Func<string, ITopic>? _getTopic;
    private bool _gloasDigest;

    /// <summary>Raised with the block decoded as the SSZ shape of its topic's fork.</summary>
    public event Action<ForkedSignedBeaconBlock>? BeaconBlockReceived;
    public event Action<SignedAggregateAndProof>? AggregateAndProofReceived;
    public event Action<SignedAggregateAndProofGloas>? GloasAggregateAndProofReceived;
    public event Action<AttesterSlashing>? AttesterSlashingReceived;
    public event Action<AttesterSlashingGloas>? GloasAttesterSlashingReceived;
    public event Action<SignedExecutionPayloadEnvelope>? ExecutionPayloadEnvelopeReceived;
    public event Action<PayloadAttestationMessage>? PayloadAttestationMessageReceived;

    public long GetDropCount(GossipDropReason reason) => Interlocked.Read(ref _dropCounts[(int)reason]);

    /// <summary>Whether a block with a valid proposer signature has been seen for <paramref name="slot"/> and <paramref name="proposerIndex"/>.</summary>
    internal bool IsProposalSeen(ulong slot, ulong proposerIndex) => _seenProposals.Get((slot, proposerIndex));

    /// <summary>Records that a block for <paramref name="slot"/> by <paramref name="proposerIndex"/> passed its proposer signature check.</summary>
    /// <remarks>The spec IGNOREs later blocks for the pair only once one with a valid signature is seen, so a forged block must never mark it.</remarks>
    internal void MarkProposalSeen(ulong slot, ulong proposerIndex) => _seenProposals.Set((slot, proposerIndex));

    /// <summary>Whether a valid envelope from <paramref name="builderIndex"/> has been seen for the block <paramref name="blockRoot"/>.</summary>
    internal bool IsEnvelopeSeen(Hash256 blockRoot, ulong builderIndex) => _seenEnvelopes.Get((blockRoot, builderIndex));

    /// <summary>Records that an envelope for <paramref name="blockRoot"/> from <paramref name="builderIndex"/> passed every gossip check, its signature included.</summary>
    /// <remarks>The spec IGNOREs later envelopes for the pair only once a valid one is seen, so an unverified envelope must never mark it.</remarks>
    internal void MarkEnvelopeSeen(Hash256 blockRoot, ulong builderIndex) => _seenEnvelopes.Set((blockRoot, builderIndex));

    /// <summary>Records the intersecting indices of an attester slashing that passed every gossip check, its signatures included.</summary>
    /// <remarks>
    /// The spec IGNOREs a later slashing only once each of its intersecting indices is in this set, so an unverified slashing
    /// must never mark them: a forged one would otherwise suppress the real slashing of the same validators.
    /// </remarks>
    internal void MarkSlashedIndicesSeen(IEnumerable<ulong> indices)
    {
        foreach (ulong index in indices)
        {
            _seenSlashedIndices.Set(index);
        }
    }

    /// <summary>Subscribes all gossip topics for <paramref name="forkDigest"/> with topics obtained from <paramref name="getTopic"/> (see <see cref="BeaconP2P.GetTopic"/>).</summary>
    /// <remarks><paramref name="forkDigest"/> also fixes the fork of the single-digest <c>Handle*</c> overloads and <see cref="HandlerFor(string)"/>.</remarks>
    public void Start(Func<string, ITopic> getTopic, byte[] forkDigest)
    {
        lock (_subscriptionLock)
        {
            _getTopic = getTopic;
            _gloasDigest = IsGloasDigest(forkDigest);
            SubscribeDigest(forkDigest);
        }
    }

    /// <summary>Subscribes all gossip topics for <paramref name="forkDigest"/>, the Gloas-only ones too when it is a Gloas digest; does nothing when they are already subscribed.</summary>
    /// <remarks>Several digests are subscribed at once around a fork digest change (see <see cref="GossipTopics.DigestsAround"/>).</remarks>
    /// <exception cref="InvalidOperationException"><see cref="Start"/> has not run.</exception>
    public void SubscribeDigest(byte[] forkDigest)
    {
        lock (_subscriptionLock)
        {
            if (_getTopic is null)
            {
                throw new InvalidOperationException($"{nameof(GossipRouter)} is not started");
            }

            string key = Convert.ToHexStringLower(forkDigest);
            if (_subscriptions.ContainsKey(key))
            {
                return;
            }

            bool gloas = IsGloasDigest(forkDigest);
            List<(ITopic Topic, Action<byte[]> Handler)> subscriptions = [];
            string[] names = gloas ? [.. GossipTopics.SubscribedTopicNames, .. GossipTopics.GloasTopicNames] : GossipTopics.SubscribedTopicNames;
            foreach (string name in names)
            {
                ITopic topic = _getTopic(GossipTopics.Topic(forkDigest, name));
                Action<byte[]> handler = HandlerFor(name, gloas);
                topic.OnMessage += handler;
                topic.Subscribe();
                subscriptions.Add((topic, handler));
            }

            _subscriptions[key] = subscriptions;
            if (_logger.IsInfo) _logger.Info($"Subscribed beacon gossip topics for fork digest 0x{key}");
        }
    }

    /// <summary>Unsubscribes the gossip topics of <paramref name="forkDigest"/> and detaches their handlers; does nothing when none are subscribed.</summary>
    public void UnsubscribeDigest(byte[] forkDigest)
    {
        lock (_subscriptionLock)
        {
            string key = Convert.ToHexStringLower(forkDigest);
            if (!_subscriptions.Remove(key, out List<(ITopic Topic, Action<byte[]> Handler)>? subscriptions))
            {
                return;
            }

            foreach ((ITopic topic, Action<byte[]> handler) in subscriptions)
            {
                topic.OnMessage -= handler;
                topic.Unsubscribe();
            }

            if (_logger.IsInfo) _logger.Info($"Unsubscribed beacon gossip topics for fork digest 0x{key}");
        }
    }

    /// <summary>The raw-payload handler for an eth2 gossip topic name of the digest passed to <see cref="Start"/>.</summary>
    public Action<byte[]> HandlerFor(string name) => HandlerFor(name, _gloasDigest);

    // The fork is bound at subscription, so a message still in flight on a rotated-out topic keeps that topic's type.
    private Action<byte[]> HandlerFor(string name, bool gloasTopic) => name switch
    {
        GossipTopics.BeaconBlock or GossipTopics.BeaconAggregateAndProof or GossipTopics.AttesterSlashing or GossipTopics.ExecutionPayload
            or GossipTopics.PayloadAttestationMessage =>
            message => Handle(name, gloasTopic, message),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown gossip topic name"),
    };

    /// <summary>Validates a raw message on the topic <paramref name="name"/> of a Fulu or Gloas digest and, when it passes, raises its typed event.</summary>
    /// <returns>
    /// <see cref="MessageValidity.Rejected"/> or <see cref="MessageValidity.Ignored"/>. A message whose event is raised is
    /// <see cref="MessageValidity.Ignored"/> too: its signature and state checks run later, so it must not be forwarded.
    /// </returns>
    internal MessageValidity Handle(string name, bool gloasTopic, byte[] message) => name switch
    {
        GossipTopics.BeaconBlock => HandleBeaconBlock(message, gloasTopic),
        GossipTopics.BeaconAggregateAndProof => gloasTopic ? HandleGloasAggregateAndProof(message) : HandleFuluAggregateAndProof(message),
        GossipTopics.AttesterSlashing => gloasTopic ? HandleGloasAttesterSlashing(message) : HandleFuluAttesterSlashing(message),
        GossipTopics.ExecutionPayload => HandleExecutionPayloadEnvelope(message),
        GossipTopics.PayloadAttestationMessage => HandlePayloadAttestationMessage(message),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown gossip topic name"),
    };

    /// <summary>Handles a <c>beacon_block</c> message on the topic of the digest passed to <see cref="Start"/>.</summary>
    public MessageValidity HandleBeaconBlock(byte[] message) => HandleBeaconBlock(message, _gloasDigest);

    // phase0 p2p: MUST reject messages containing an incorrect type, so the topic's fork fixes the decoded shape.
    private MessageValidity HandleBeaconBlock(byte[] message, bool gloasTopic) =>
        Handle(GossipTopics.BeaconBlock, message,
            payload => DecodeBlock(payload, gloasTopic),
            block => ValidateBlock(block, gloasTopic),
            block => BeaconBlockReceived?.Invoke(block));

    /// <summary>Handles a <c>beacon_aggregate_and_proof</c> message on the topic of the digest passed to <see cref="Start"/>.</summary>
    public MessageValidity HandleAggregateAndProof(byte[] message) =>
        _gloasDigest ? HandleGloasAggregateAndProof(message) : HandleFuluAggregateAndProof(message);

    private MessageValidity HandleFuluAggregateAndProof(byte[] message) =>
        Handle(GossipTopics.BeaconAggregateAndProof, message,
            static payload => { SignedAggregateAndProof.Decode(payload, out SignedAggregateAndProof aggregate); return aggregate; },
            aggregate => ValidateAggregate(aggregate.Message!.Aggregate!.Data!, aggregate.Message.Aggregate.CommitteeBits!, aggregate.Message.Aggregate.AggregationBits!, gloas: false),
            aggregate => AggregateAndProofReceived?.Invoke(aggregate));

    private MessageValidity HandleGloasAggregateAndProof(byte[] message) =>
        Handle(GossipTopics.BeaconAggregateAndProof, message,
            static payload => { SignedAggregateAndProofGloas.Decode(payload, out SignedAggregateAndProofGloas aggregate); return aggregate; },
            aggregate => ValidateAggregate(aggregate.Message!.Aggregate!.Data!, aggregate.Message.Aggregate.CommitteeBits!, aggregate.Message.Aggregate.AggregationBits!, gloas: true),
            aggregate => GloasAggregateAndProofReceived?.Invoke(aggregate),
            MaxSignedAggregateAndProofSizeGloas);

    /// <summary>Handles an <c>attester_slashing</c> message on the topic of the digest passed to <see cref="Start"/>.</summary>
    public MessageValidity HandleAttesterSlashing(byte[] message) =>
        _gloasDigest ? HandleGloasAttesterSlashing(message) : HandleFuluAttesterSlashing(message);

    private MessageValidity HandleFuluAttesterSlashing(byte[] message) =>
        Handle(GossipTopics.AttesterSlashing, message,
            static payload => { AttesterSlashing.Decode(payload, out AttesterSlashing slashing); return slashing; },
            slashing => ValidateAttesterSlashing(
                slashing.Attestation1!.AttestingIndices!, slashing.Attestation1.Data!, slashing.Attestation2!.AttestingIndices!, slashing.Attestation2.Data!, gloas: false),
            slashing => AttesterSlashingReceived?.Invoke(slashing));

    private MessageValidity HandleGloasAttesterSlashing(byte[] message) =>
        Handle(GossipTopics.AttesterSlashing, message,
            static payload => { AttesterSlashingGloas.Decode(payload, out AttesterSlashingGloas slashing); return slashing; },
            slashing => ValidateAttesterSlashing(
                slashing.Attestation1!.AttestingIndices!, slashing.Attestation1.Data!, slashing.Attestation2!.AttestingIndices!, slashing.Attestation2.Data!, gloas: true),
            slashing => GloasAttesterSlashingReceived?.Invoke(slashing),
            MaxAttesterSlashingSizeGloas);

    /// <summary>
    /// Runs every <c>execution_payload</c> rule except the envelope signature, which needs the block's state: an envelope
    /// passing them, or naming a block not held yet, is raised for the import pipeline to verify and is never forwarded.
    /// </summary>
    public MessageValidity HandleExecutionPayloadEnvelope(byte[] message) =>
        Handle(GossipTopics.ExecutionPayload, message,
            static payload => { SignedExecutionPayloadEnvelope.Decode(payload, out SignedExecutionPayloadEnvelope envelope); return envelope; },
            ValidateEnvelope,
            envelope => ExecutionPayloadEnvelopeReceived?.Invoke(envelope));

    /// <summary>
    /// Runs every <c>payload_attestation_message</c> rule except PTC membership and the signature, which need the block's state:
    /// a vote passing them is raised for fork choice to verify and is never forwarded.
    /// </summary>
    /// <remarks>
    /// Only the first vote raised for a (slot, validator) pair is verified, though the spec IGNOREs a repeat only after a valid one:
    /// the signature cannot be checked here, and each forged vote under a PTC member's index would cost fork choice a BLS verify.
    /// </remarks>
    private MessageValidity HandlePayloadAttestationMessage(byte[] message) =>
        Handle(GossipTopics.PayloadAttestationMessage, message,
            static payload => { PayloadAttestationMessage.Decode(payload, out PayloadAttestationMessage vote); return vote; },
            ValidatePayloadAttestation,
            vote => PayloadAttestationMessageReceived?.Invoke(vote),
            PayloadAttestationMessageSize,
            _seenPayloadAttestationMessages);

    private MessageValidity Handle<T>(string name, byte[] message, Func<byte[], T> decode, Func<T, Verdict?> validate, Action<T> raise, int maxSize = Eth2MessageId.MaxGossipSize,
        LruKeyCache<ValueHash256>? seenMessages = null) where T : class
    {
        seenMessages ??= _seenMessages;
        ReleaseDueMessages();
        Metrics.BeaconChainGossipReceivedByTopic.Increment(new StringLabel(name));

        // phase0 p2p "Gossipsub size limits": the compressed payload is bounded by max_compressed_len(MAX_PAYLOAD_SIZE); the type bound caps only the uncompressed size.
        if (message.Length > Eth2MessageId.MaxCompressedGossipSize)
        {
            return Drop(name, GossipDropReason.Oversized, MessageValidity.Rejected);
        }

        SnappyDecodeResult snappy = Eth2MessageId.TryDecompress(message, maxSize, out byte[]? payload);
        if (snappy != SnappyDecodeResult.Decoded)
        {
            return Drop(name, snappy == SnappyDecodeResult.Oversized ? GossipDropReason.Oversized : GossipDropReason.InvalidSnappy, MessageValidity.Rejected);
        }

        ValueHash256 seenKey = SeenKey(name, payload!);
        if (seenMessages.Get(seenKey))
        {
            return Drop(name, GossipDropReason.Duplicate, MessageValidity.Ignored);
        }

        T value;
        Verdict? verdict;
        try
        {
            value = decode(payload!);
            verdict = validate(value);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Malformed {name} gossip message: {e.Message}");
            return Drop(name, GossipDropReason.InvalidSsz, MessageValidity.Rejected);
        }

        if (verdict is { DeferToSlot: null } drop)
        {
            return Drop(name, drop.Reason, drop.Validity);
        }

        // Messages are only marked as seen once they pass, so a dropped message can still be delivered
        // by a later copy under another message id. Set returns false when a concurrent handler raced us.
        if (!seenMessages.Set(seenKey))
        {
            return Drop(name, GossipDropReason.Duplicate, MessageValidity.Ignored);
        }

        if (verdict?.DeferToSlot is { } slot)
        {
            return Defer(slot, () => raise(value)) ? MessageValidity.Ignored : Drop(name, GossipDropReason.FutureSlot, MessageValidity.Ignored);
        }

        Metrics.BeaconChainGossipAccepted++;
        raise(value);
        return MessageValidity.Ignored;
    }

    // validate_beacon_block_gossip: seen, future and finalized IGNOREs, then (Gloas) the two limit REJECTs. The parent-slot, bid-parent,
    // payload-timestamp and blob-count REJECTs need no state, and gossip_validation.md lets independent conditions run in any order.
    private Verdict? ValidateBlock(ForkedSignedBeaconBlock block, bool gloasTopic)
    {
        ulong slot = block.Slot;
        if (IsProposalSeen(slot, block.ProposerIndex))
        {
            return Verdict.Ignore(GossipDropReason.Duplicate);
        }

        Verdict? timing = CheckNotFromFuture(slot);
        if (timing is { DeferToSlot: null })
        {
            return timing;
        }

        if (slot <= FinalizedStartSlot)
        {
            return Verdict.Ignore(GossipDropReason.BeforeFinalized);
        }

        ulong currentSlot = slotClock.CurrentSlot;
        if (currentSlot > spec.SlotsPerEpoch && slot < currentSlot - spec.SlotsPerEpoch)
        {
            return Verdict.Ignore(GossipDropReason.StaleSlot);
        }

        if (SignedBeaconBlockCodec.IsGloasSlot(slot, spec) != gloasTopic)
        {
            return Verdict.Ignore(GossipDropReason.InvalidField);
        }

        BeaconBlockBodyGloas? gloasBody = block is ForkedSignedBeaconBlock.OfGloas { Block.Message.Body: { } gloas } ? gloas : null;
        if (gloasBody is not null)
        {
            try
            {
                GloasBlockProcessing.VerifyBlockBodyOperationLimits(gloasBody);
                GloasBlockProcessing.VerifyExecutionRequestsLimits(gloasBody.ParentExecutionRequests ?? new ExecutionRequestsGloas());
            }
            catch (BeaconStateException)
            {
                return Invalid(GossipDropReason.LimitExceeded);
            }
        }

        // The store holds only blocks fork choice accepted, so a held parent has been seen and passed validation.
        bool parentHeld = store?.HasBlock(block.ParentRoot) == true;
        if (parentHeld && _storedBlockSlots!.TryRead(block.ParentRoot, _parentSlotReads, out ulong parentSlot) && slot <= parentSlot)
        {
            return Invalid(GossipDropReason.NotAboveParentSlot);
        }

        ulong blobCount;
        if (gloasBody is not null)
        {
            ExecutionPayloadBid bid = gloasBody.SignedExecutionPayloadBid!.Message!;
            // The spec's parent-not-seen IGNORE comes first; the gossip_validation vectors hold an unseen parent to it whatever the bid names.
            if (bid.ParentBlockRoot != block.ParentRoot)
            {
                return parentHeld ? Invalid(GossipDropReason.InvalidField) : Verdict.Ignore(GossipDropReason.InvalidField);
            }

            blobCount = (ulong)(bid.BlobKzgCommitments?.Length ?? 0);
        }
        else
        {
            BeaconBlockBody body = ((ForkedSignedBeaconBlock.OfFulu)block).Block.Message!.Body!;

            // bellatrix p2p: the payload timestamp equals compute_time_at_slot, with genesis_time fixed by the network.
            if (body.ExecutionPayload!.Timestamp != (UInt128)spec.GenesisTime + (UInt128)slot * spec.SecondsPerSlot)
            {
                return Invalid(GossipDropReason.InvalidField);
            }

            blobCount = (ulong)(body.BlobKzgCommitments?.Length ?? 0);
        }

        return blobCount > (spec.GetBlobParameters(spec.GetEpoch(slot))?.MaxBlobsPerBlock ?? spec.MaxBlobsPerBlockElectra)
            ? Invalid(GossipDropReason.LimitExceeded)
            : timing;

        // An early next-slot block has not yet passed the future-slot IGNORE ordered before every REJECT.
        Verdict Invalid(GossipDropReason reason) => timing is null ? Verdict.Reject(reason) : Verdict.Ignore(reason);
    }

    private static ForkedSignedBeaconBlock DecodeBlock(byte[] payload, bool gloasTopic)
    {
        if (gloasTopic)
        {
            SignedBeaconBlockGloas.Decode(payload, out SignedBeaconBlockGloas gloas);
            return new ForkedSignedBeaconBlock.OfGloas(gloas);
        }

        SignedBeaconBlock.Decode(payload, out SignedBeaconBlock fulu);
        return new ForkedSignedBeaconBlock.OfFulu(fulu);
    }

    // validate_beacon_aggregate_and_proof_gossip: the index and committee-bit REJECTs come first. The target-epoch and
    // participant REJECTs need no state (no set bit means no participant), so they follow only the clock IGNOREs.
    private Verdict? ValidateAggregate(AttestationData data, BitArray committeeBits, BitArray aggregationBits, bool gloas)
    {
        if (gloas ? data.Index > 1 : data.Index != 0)
        {
            return Verdict.Reject(GossipDropReason.InvalidField);
        }

        if (CountSetBits(committeeBits) != 1)
        {
            return Verdict.Reject(GossipDropReason.InvalidField);
        }

        Verdict? timing = CheckNotFromFuture(data.Slot);
        if (timing is { DeferToSlot: null })
        {
            return timing;
        }

        ulong epoch = spec.GetEpoch(data.Slot);
        if (!IsCurrentOrPreviousEpoch(epoch))
        {
            return Verdict.Ignore(GossipDropReason.StaleSlot);
        }

        if (data.Target!.Epoch != epoch || CountSetBits(aggregationBits) == 0 || (gloas && data.Index != 0 && IsVoteForHeldBlockAtItsSlot(data)))
        {
            // An early next-slot aggregate has not yet passed the future-slot IGNORE ordered before these REJECTs.
            return timing is null ? Verdict.Reject(GossipDropReason.InvalidField) : Verdict.Ignore(GossipDropReason.InvalidField);
        }

        return timing;
    }

    // gloas verify_attestation_payload_status: a same-slot vote cannot see the payload yet. A block not held is left to fork choice.
    private bool IsVoteForHeldBlockAtItsSlot(AttestationData data) =>
        _storedBlockSlots?.TryRead(data.BeaconBlockRoot!, _votedBlockSlotReads, out ulong blockSlot) == true
        && blockSlot == data.Slot;

    // phase0 attester_slashing: IGNORE unless an intersecting index is not in the seen set, then REJECT non-slashable data.
    private Verdict? ValidateAttesterSlashing(ulong[] indices1, AttestationData data1, ulong[] indices2, AttestationData data2, bool gloas)
    {
        HashSet<ulong> second = [.. indices2];
        bool intersects = false;
        bool hasNewIndex = false;
        foreach (ulong index in indices1)
        {
            if (second.Contains(index))
            {
                intersects = true;
                if (!_seenSlashedIndices.Get(index))
                {
                    hasNewIndex = true;
                    break;
                }
            }
        }

        if (!hasNewIndex)
        {
            return Verdict.Ignore(intersects ? GossipDropReason.Duplicate : GossipDropReason.InvalidField);
        }

        if (!BeaconStateAccessors.IsSlashableAttestationData(data1, data2))
        {
            return Verdict.Reject(GossipDropReason.InvalidField);
        }

        return HasValidIndices(indices1, gloas) && HasValidIndices(indices2, gloas) ? null : Verdict.Reject(GossipDropReason.InvalidField);

        // is_valid_indexed_attestation index rules needing no state: sorted and unique, and in Gloas (EIP-7688) within the committee bound.
        static bool HasValidIndices(ulong[] indices, bool gloas)
        {
            if (gloas && indices.Length > Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot)
            {
                return false;
            }

            for (int i = 1; i < indices.Length; i++)
            {
                if (indices[i] <= indices[i - 1])
                {
                    return false;
                }
            }

            return true;
        }
    }

    // validate_execution_payload_envelope_gossip (gloas/p2p-interface.md). verify_execution_requests_limits MAY run at
    // deserialization, so it rejects first. The store holds only blocks fork choice accepted, so a held block stands in for
    // both "seen" and "passes validation"; an unheld one MAY be queued until it is retrieved, so the envelope is consumed.
    private Verdict? ValidateEnvelope(SignedExecutionPayloadEnvelope envelope)
    {
        ExecutionPayloadEnvelope message = envelope.Message!;
        ExecutionRequestsGloas requests = message.ExecutionRequests ?? new ExecutionRequestsGloas();
        try
        {
            GloasBlockProcessing.VerifyExecutionRequestsLimits(requests);
        }
        catch (BeaconStateException)
        {
            return Verdict.Reject(GossipDropReason.LimitExceeded);
        }

        Hash256 blockRoot = message.BeaconBlockRoot!;
        if (IsEnvelopeSeen(blockRoot, message.BuilderIndex))
        {
            return Verdict.Ignore(GossipDropReason.Duplicate);
        }

        ExecutionPayloadGloas payload = message.Payload!;
        switch (ReadEnvelopeBlock(blockRoot, payload.SlotNumber, out EnvelopeBlock? block))
        {
            // Handle does not pass this topic's fork, so an unheld envelope for a pre-Gloas slot is dropped by its payload slot
            // rather than queued; a held Gloas block reaches the spec's slot-match REJECT instead.
            case EnvelopeBlockLookup.NotHeld:
                return SignedBeaconBlockCodec.IsGloasSlot(payload.SlotNumber, spec) ? null : Verdict.Ignore(GossipDropReason.InvalidField);
            case EnvelopeBlockLookup.BudgetSpent:
                return Verdict.Ignore(GossipDropReason.StoreDecodeBudgetSpent);
        }

        if (!block!.IsGloas)
        {
            return Verdict.Ignore(GossipDropReason.InvalidField);
        }

        if (payload.SlotNumber < FinalizedStartSlot)
        {
            return Verdict.Ignore(GossipDropReason.BeforeFinalized);
        }

        if (block.Slot != payload.SlotNumber
            || message.BuilderIndex != block.BuilderIndex
            || payload.BlockHash != block.BlockHash
            || SszRoots.HashTreeRoot(requests) != block.ExecutionRequestsRoot)
        {
            return Verdict.Reject(GossipDropReason.InvalidField);
        }

        return (payload.Withdrawals?.Length ?? 0) > Presets.MaxWithdrawalsPerPayload ? Verdict.Reject(GossipDropReason.LimitExceeded) : null;
    }

    // validate_payload_attestation_message_gossip (gloas/p2p-interface.md). The store holds only blocks fork choice accepted, so
    // a held block stands in for both "seen" and "passes validation". A block slot the budget cannot read is checked by fork choice.
    private Verdict? ValidatePayloadAttestation(PayloadAttestationMessage vote)
    {
        PayloadAttestationData data = vote.Data!;
        if (spec.GetEpoch(data.Slot) < spec.GloasForkEpoch)
        {
            return Verdict.Reject(GossipDropReason.InvalidField);
        }

        if (CheckCurrentSlot(data.Slot) is { } timing)
        {
            return timing;
        }

        if (store is null)
        {
            return ClaimPayloadAttestationPair(vote);
        }

        Hash256 blockRoot = data.BeaconBlockRoot!;
        if (!store.HasBlock(blockRoot))
        {
            return Verdict.Ignore(GossipDropReason.InvalidField);
        }

        return _storedBlockSlots!.TryRead(blockRoot, _ptcBlockSlotReads, out ulong blockSlot) && blockSlot != data.Slot
            ? Verdict.Ignore(GossipDropReason.InvalidField)
            : ClaimPayloadAttestationPair(vote);
    }

    // One atomic claim per (slot, validator): concurrent handlers cannot both pass, so the pair pays one signature check whatever the signature.
    private Verdict? ClaimPayloadAttestationPair(PayloadAttestationMessage vote) =>
        _seenPayloadAttestations.Set((vote.Data!.Slot, vote.ValidatorIndex)) ? null : Verdict.Ignore(GossipDropReason.Duplicate);

    // altair p2p is_current_slot: start(slot) - MAXIMUM_GOSSIP_CLOCK_DISPARITY <= now <= start(slot + 1) + MAXIMUM_GOSSIP_CLOCK_DISPARITY.
    private Verdict? CheckCurrentSlot(ulong slot)
    {
        long now = slotClock.UnixMilliseconds;
        // Checked before any slot-start time, which a wire slot near ulong.MaxValue would overflow.
        if (slot > slotClock.CurrentSlot + 1 || now + MaximumGossipClockDisparityMs < slotClock.SlotStartMilliseconds(slot))
        {
            return Verdict.Ignore(GossipDropReason.FutureSlot);
        }

        return now > slotClock.SlotStartMilliseconds(slot + 1) + MaximumGossipClockDisparityMs ? Verdict.Ignore(GossipDropReason.StaleSlot) : null;
    }

    private EnvelopeBlockLookup ReadEnvelopeBlock(Hash256 blockRoot, ulong payloadSlot, out EnvelopeBlock? block)
    {
        if (_envelopeBlocks.TryGet(blockRoot, out block))
        {
            return EnvelopeBlockLookup.Found;
        }

        if (store is null || !store.HasBlock(blockRoot))
        {
            return EnvelopeBlockLookup.NotHeld;
        }

        if (!IsCanonicalAtRecentSlot(blockRoot, payloadSlot) && !_envelopeBlockDecodes.TryTake(slotClock.CurrentSlot))
        {
            return EnvelopeBlockLookup.BudgetSpent;
        }

        // A corrupt record reads as a held non-Gloas block; any other store fault is a node failure and surfaces through Handle as a REJECT.
        ForkedSignedBeaconBlock? forked;
        try
        {
            if (!store.TryGetForkedBlock(blockRoot, out forked))
            {
                return EnvelopeBlockLookup.NotHeld;
            }
        }
        catch (Exception e) when (e is BeaconStateException or InvalidDataException)
        {
            if (_logger.IsWarn) _logger.Warn($"Unreadable stored block {blockRoot} named by an execution payload envelope: {e.Message}");
            forked = null;
        }

        block = forked is ForkedSignedBeaconBlock.OfGloas { Block.Message: { Body.SignedExecutionPayloadBid.Message: { } bid } gloas }
            ? new EnvelopeBlock(true, gloas.Slot, bid.BuilderIndex, bid.BlockHash, bid.ExecutionRequestsRoot)
            : EnvelopeBlock.NotGloas;
        _envelopeBlocks.Set(blockRoot, block);
        return EnvelopeBlockLookup.Found;
    }

    // Only exempts a decode from the budget: the decoded block is still checked against the envelope.
    private bool IsCanonicalAtRecentSlot(Hash256 blockRoot, ulong payloadSlot)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        return (payloadSlot == currentSlot || payloadSlot + 1 == currentSlot)
            && store!.TryGetCanonicalRoot(payloadSlot, out Hash256? canonical)
            && canonical == blockRoot;
    }

    private ulong FinalizedStartSlot => status is null ? 0 : BeaconStateAccessors.ComputeStartSlotAtEpoch(status.CurrentStatus.FinalizedEpoch);

    // Null once the slot has started, within MAXIMUM_GOSSIP_CLOCK_DISPARITY. An early next-slot message is deferred rather
    // than dropped, because the pubsub library never redelivers a message id it was told to ignore.
    private Verdict? CheckNotFromFuture(ulong slot)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        if (slot <= currentSlot)
        {
            return null;
        }

        if (slot != currentSlot + 1)
        {
            return Verdict.Ignore(GossipDropReason.FutureSlot);
        }

        return slotClock.MillisecondsToNextSlot <= MaximumGossipClockDisparityMs ? null : Verdict.DeferTo(slot);
    }

    // deneb p2p is_current_or_previous_epoch: start(epoch) - disparity <= now <= start(epoch + 2) + disparity, both ends inclusive.
    private bool IsCurrentOrPreviousEpoch(ulong epoch)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        long now = slotClock.UnixMilliseconds;
        ulong latestSlot = now + MaximumGossipClockDisparityMs >= slotClock.SlotStartMilliseconds(currentSlot + 1) ? currentSlot + 1 : currentSlot;
        ulong earliestSlot = currentSlot > 0 && now - MaximumGossipClockDisparityMs <= slotClock.SlotStartMilliseconds(currentSlot) ? currentSlot - 1 : currentSlot;
        return epoch <= spec.GetEpoch(latestSlot) && epoch + 1 >= spec.GetEpoch(earliestSlot);
    }

    private bool Defer(ulong slot, Action raise)
    {
        lock (_deferredLock)
        {
            if (_deferred.Count >= MaxDeferredMessages)
            {
                return false;
            }

            _deferred.Add((slot, raise));
            Volatile.Write(ref _deferredCount, _deferred.Count);
            return true;
        }
    }

    /// <summary>Raises the held next-slot messages whose slot has started.</summary>
    /// <remarks>Runs before every handled message and on each slot tick, so a held message does not wait for later traffic.</remarks>
    internal void ReleaseDueMessages()
    {
        if (Volatile.Read(ref _deferredCount) == 0)
        {
            return;
        }

        List<Action>? due = null;
        lock (_deferredLock)
        {
            for (int i = _deferred.Count - 1; i >= 0; i--)
            {
                if (CheckNotFromFuture(_deferred[i].Slot) is null)
                {
                    (due ??= []).Insert(0, _deferred[i].Raise);
                    _deferred.RemoveAt(i);
                }
            }

            Volatile.Write(ref _deferredCount, _deferred.Count);
        }

        foreach (Action raise in due ?? [])
        {
            Metrics.BeaconChainGossipAccepted++;
            raise();
        }
    }

    private static int CountSetBits(BitArray bits)
    {
        int count = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether <paramref name="digest"/> is one of the digests in effect from the Gloas fork onward.</summary>
    private bool IsGloasDigest(byte[] digest)
    {
        foreach (ulong epoch in GossipTopics.DigestRotationEpochs(spec, spec.GloasForkEpoch))
        {
            if (ForkDigest.Compute(spec, epoch).AsSpan().SequenceEqual(digest))
            {
                return true;
            }
        }

        return false;
    }

    private MessageValidity Drop(string name, GossipDropReason reason, MessageValidity validity)
    {
        Drop(name, reason);
        return validity;
    }

    /// <summary>Counts a message on the topic <paramref name="name"/> dropped before it reached a typed event.</summary>
    internal void Drop(string name, GossipDropReason reason)
    {
        Metrics.BeaconChainGossipDropped++;
        Metrics.BeaconChainGossipRejectedByTopic.Increment(new GossipRejectKey(name, reason));
        Interlocked.Increment(ref _dropCounts[(int)reason]);
        if (_logger.IsTrace) _logger.Trace($"Dropped {name} gossip message: {reason}");
    }

    private static ValueHash256 SeenKey(string name, byte[] payload)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(name));
        sha.AppendData(payload);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        sha.GetHashAndReset(hash);
        return new ValueHash256(hash);
    }

    private enum EnvelopeBlockLookup
    {
        Found,
        NotHeld,
        BudgetSpent,
    }

    /// <summary>The bid fields of a held block that an envelope is checked against; <see cref="IsGloas"/> is false for a held block with no readable bid.</summary>
    private sealed record EnvelopeBlock(bool IsGloas, ulong Slot, ulong BuilderIndex, Hash256? BlockHash, Hash256? ExecutionRequestsRoot)
    {
        public static readonly EnvelopeBlock NotGloas = new(false, 0, 0, null, null);
    }

    private readonly record struct Verdict(GossipDropReason Reason, MessageValidity Validity, ulong? DeferToSlot = null)
    {
        public static Verdict Reject(GossipDropReason reason) => new(reason, MessageValidity.Rejected);

        public static Verdict Ignore(GossipDropReason reason) => new(reason, MessageValidity.Ignored);

        public static Verdict DeferTo(ulong slot) => new(GossipDropReason.FutureSlot, MessageValidity.Ignored, slot);
    }
}
