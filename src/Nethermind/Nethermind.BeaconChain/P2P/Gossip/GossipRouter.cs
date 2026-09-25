// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
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
/// <param name="status">Where the finalized checkpoint is read from; <c>null</c> applies no finalized-slot rule.</param>
public sealed class GossipRouter(BeaconChainSpec spec, SlotClock slotClock, ILogManager logManager, ExecutionPayloadEnvelopePool? envelopePool = null, IBeaconChainStatusSource? status = null)
{
    /// <summary>The spec <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c>.</summary>
    public const long MaximumGossipClockDisparityMs = 500;

    // gloas/p2p-interface.md type-specific SSZ bounds: the Gloas lists are progressive, so the types themselves carry none.
    private const int MaxSignedAggregateAndProofSizeGloas = 16829;
    private const int MaxAttesterSlashingSizeGloas = 2097616;

    private const int SeenCacheSize = 2048;
    private const int SeenProposalCacheSize = 1024;
    private const int MaxDeferredMessages = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<GossipRouter>();
    private readonly LruKeyCache<ValueHash256> _seenMessages = new(SeenCacheSize, "beacon gossip seen messages");
    private readonly long[] _dropCounts = new long[Enum.GetValues<GossipDropReason>().Length];
    private readonly Lock _subscriptionLock = new();
    private readonly List<(ITopic Topic, Action<byte[]> Handler)> _subscriptions = [];

    // Written by the import worker once a block's proposer signature verifies; read on the network thread.
    private readonly LruKeyCache<(ulong Slot, ulong Proposer)> _seenProposals = new(SeenProposalCacheSize, "beacon gossip proposals");
    private readonly Lock _deferredLock = new();
    private readonly List<(ulong Slot, Action Raise)> _deferred = [];
    private int _deferredCount;

    private Func<string, ITopic>? _getTopic;
    private byte[] _currentForkDigest = [];
    private bool _gloasDigest;
    private bool _gloasActive;

    /// <summary>Raised with the block decoded as the SSZ shape of its topic's fork.</summary>
    public event Action<ForkedSignedBeaconBlock>? BeaconBlockReceived;
    public event Action<SignedAggregateAndProof>? AggregateAndProofReceived;
    public event Action<SignedAggregateAndProofGloas>? GloasAggregateAndProofReceived;
    public event Action<AttesterSlashing>? AttesterSlashingReceived;
    public event Action<AttesterSlashingGloas>? GloasAttesterSlashingReceived;
    public event Action<SignedExecutionPayloadEnvelope>? ExecutionPayloadEnvelopeReceived;

    public long GetDropCount(GossipDropReason reason) => Interlocked.Read(ref _dropCounts[(int)reason]);

    /// <summary>Whether a block with a valid proposer signature has been seen for <paramref name="slot"/> and <paramref name="proposerIndex"/>.</summary>
    internal bool IsProposalSeen(ulong slot, ulong proposerIndex) => _seenProposals.Get((slot, proposerIndex));

    /// <summary>Records that a block for <paramref name="slot"/> by <paramref name="proposerIndex"/> passed its proposer signature check.</summary>
    /// <remarks>The spec IGNOREs later blocks for the pair only once one with a valid signature is seen, so a forged block must never mark it.</remarks>
    internal void MarkProposalSeen(ulong slot, ulong proposerIndex) => _seenProposals.Set((slot, proposerIndex));

    /// <summary>Subscribes all gossip topics for <paramref name="forkDigest"/> with topics obtained from <paramref name="getTopic"/> (see <see cref="BeaconP2P.GetTopic"/>).</summary>
    public void Start(Func<string, ITopic> getTopic, byte[] forkDigest)
    {
        lock (_subscriptionLock)
        {
            _getTopic = getTopic;
            SubscribeTopics(forkDigest);
        }
    }

    /// <summary>Moves all subscriptions to a new fork digest at a fork activation or EIP-7892 BPO boundary.</summary>
    public void RotateDigest(byte[] newForkDigest)
    {
        lock (_subscriptionLock)
        {
            if (_getTopic is null)
            {
                throw new InvalidOperationException($"{nameof(GossipRouter)} is not started");
            }

            foreach ((ITopic topic, Action<byte[]> handler) in _subscriptions)
            {
                topic.OnMessage -= handler;
                topic.Unsubscribe();
            }

            _subscriptions.Clear();
            SubscribeTopics(newForkDigest);
        }
    }

    private void SubscribeTopics(byte[] forkDigest)
    {
        _currentForkDigest = forkDigest;
        _gloasDigest = IsGloasDigest(forkDigest);
        foreach (string name in CurrentTopicNames())
        {
            ITopic topic = _getTopic!(GossipTopics.Topic(forkDigest, name));
            Action<byte[]> handler = HandlerFor(name);
            topic.OnMessage += handler;
            topic.Subscribe();
            _subscriptions.Add((topic, handler));
        }

        if (_logger.IsInfo) _logger.Info($"Subscribed beacon gossip topics for fork digest 0x{Convert.ToHexStringLower(forkDigest)}");
    }

    /// <summary>The pre-Gloas topic names, plus the Gloas-only ones once <see cref="ActivateGloasTopics"/> has run.</summary>
    private IEnumerable<string> CurrentTopicNames() =>
        _gloasActive ? [.. GossipTopics.SubscribedTopicNames, .. GossipTopics.GloasTopicNames] : GossipTopics.SubscribedTopicNames;

    /// <summary>
    /// Adds the Gloas-only gossip topics (<see cref="GossipTopics.GloasTopicNames"/>)
    /// under the currently subscribed fork digest. Call once, at the Gloas fork boundary: a later
    /// <see cref="RotateDigest"/> (e.g. an EIP-7892 BPO rotation past Gloas) carries them forward
    /// automatically since <see cref="CurrentTopicNames"/> includes them from then on, rather than the
    /// subscription set being fixed at <see cref="Start"/> time.
    /// </summary>
    public void ActivateGloasTopics()
    {
        lock (_subscriptionLock)
        {
            if (_getTopic is null)
            {
                throw new InvalidOperationException($"{nameof(GossipRouter)} is not started");
            }

            if (_gloasActive)
            {
                return;
            }

            _gloasActive = true;
            foreach (string name in GossipTopics.GloasTopicNames)
            {
                ITopic topic = _getTopic(GossipTopics.Topic(_currentForkDigest, name));
                Action<byte[]> handler = HandlerFor(name);
                topic.OnMessage += handler;
                topic.Subscribe();
                _subscriptions.Add((topic, handler));
            }

            if (_logger.IsInfo) _logger.Info("Activated Gloas beacon gossip topics (execution_payload)");
        }
    }

    /// <summary>The raw-payload handler for an eth2 gossip topic name.</summary>
    public Action<byte[]> HandlerFor(string name) => HandlerFor(name, _gloasDigest);

    // The fork is bound at subscription, so a message still in flight on a rotated-out topic keeps that topic's type.
    private Action<byte[]> HandlerFor(string name, bool gloasTopic) => name switch
    {
        GossipTopics.BeaconBlock or GossipTopics.BeaconAggregateAndProof or GossipTopics.AttesterSlashing or GossipTopics.ExecutionPayload =>
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
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown gossip topic name"),
    };

    /// <summary>Handles a <c>beacon_block</c> message on the currently subscribed digest's topic.</summary>
    public MessageValidity HandleBeaconBlock(byte[] message) => HandleBeaconBlock(message, _gloasDigest);

    // phase0 p2p: MUST reject messages containing an incorrect type, so the topic's fork fixes the decoded shape.
    private MessageValidity HandleBeaconBlock(byte[] message, bool gloasTopic) =>
        Handle(GossipTopics.BeaconBlock, message,
            payload => DecodeBlock(payload, gloasTopic),
            block => ValidateBlock(block, gloasTopic),
            block => BeaconBlockReceived?.Invoke(block));

    /// <summary>Handles a <c>beacon_aggregate_and_proof</c> message on the currently subscribed digest's topic.</summary>
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

    /// <summary>Handles an <c>attester_slashing</c> message on the currently subscribed digest's topic.</summary>
    public MessageValidity HandleAttesterSlashing(byte[] message) =>
        _gloasDigest ? HandleGloasAttesterSlashing(message) : HandleFuluAttesterSlashing(message);

    private MessageValidity HandleFuluAttesterSlashing(byte[] message) =>
        Handle(GossipTopics.AttesterSlashing, message,
            static payload => { AttesterSlashing.Decode(payload, out AttesterSlashing slashing); return slashing; },
            static slashing => ValidateAttesterSlashing(
                slashing.Attestation1!.AttestingIndices!, slashing.Attestation1.Data!, slashing.Attestation2!.AttestingIndices!, slashing.Attestation2.Data!),
            slashing => AttesterSlashingReceived?.Invoke(slashing));

    private MessageValidity HandleGloasAttesterSlashing(byte[] message) =>
        Handle(GossipTopics.AttesterSlashing, message,
            static payload => { AttesterSlashingGloas.Decode(payload, out AttesterSlashingGloas slashing); return slashing; },
            static slashing => ValidateAttesterSlashing(
                slashing.Attestation1!.AttestingIndices!, slashing.Attestation1.Data!, slashing.Attestation2!.AttestingIndices!, slashing.Attestation2.Data!),
            slashing => GloasAttesterSlashingReceived?.Invoke(slashing),
            MaxAttesterSlashingSizeGloas);

    /// <summary>
    /// Only the stateless <c>execution_payload</c> rules run here: the envelope's block passing validation,
    /// builder/block-hash/execution-requests-root against the committed bid, and the envelope signature all
    /// need the block and its state, so - like <see cref="HandleBeaconBlock(byte[])"/> - they are left to the
    /// orchestrator import pipeline.
    /// </summary>
    public MessageValidity HandleExecutionPayloadEnvelope(byte[] message) =>
        Handle(GossipTopics.ExecutionPayload, message,
            static payload => { SignedExecutionPayloadEnvelope.Decode(payload, out SignedExecutionPayloadEnvelope envelope); return envelope; },
            ValidateEnvelope,
            envelope =>
            {
                if (envelope.Message is { BeaconBlockRoot: { } root, Payload: { } payload })
                {
                    envelopePool?.Add(root, payload.SlotNumber, envelope);
                }

                ExecutionPayloadEnvelopeReceived?.Invoke(envelope);
            });

    private MessageValidity Handle<T>(string name, byte[] message, Func<byte[], T> decode, Func<T, Verdict?> validate, Action<T> raise, int maxSize = Eth2MessageId.MaxGossipSize) where T : class
    {
        ReleaseDueMessages();
        Metrics.BeaconChainGossipReceivedByTopic.Increment(new StringLabel(name));

        SnappyDecodeResult snappy = Eth2MessageId.TryDecompress(message, maxSize, out byte[]? payload);
        if (snappy != SnappyDecodeResult.Decoded)
        {
            return Drop(name, snappy == SnappyDecodeResult.Oversized ? GossipDropReason.Oversized : GossipDropReason.InvalidSnappy, MessageValidity.Rejected);
        }

        ValueHash256 seenKey = SeenKey(name, payload!);
        if (_seenMessages.Get(seenKey))
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
        if (!_seenMessages.Set(seenKey))
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

    // validate_beacon_block_gossip: seen, future and finalized IGNOREs, then (Gloas) the two limit REJECTs; every later
    // rule follows a store check, so its failure only drops the message.
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

        ulong blobCount;
        if (block is ForkedSignedBeaconBlock.OfGloas { Block.Message.Body: { } gloasBody })
        {
            try
            {
                GloasBlockProcessing.VerifyBlockBodyOperationLimits(gloasBody);
                GloasBlockProcessing.VerifyExecutionRequestsLimits(gloasBody.ParentExecutionRequests ?? new ExecutionRequestsGloas());
            }
            catch (BeaconStateException)
            {
                // An early next-slot block has not yet passed the future-slot IGNORE ordered before this REJECT.
                return timing is null ? Verdict.Reject(GossipDropReason.LimitExceeded) : Verdict.Ignore(GossipDropReason.LimitExceeded);
            }

            ExecutionPayloadBid bid = gloasBody.SignedExecutionPayloadBid!.Message!;
            if (bid.ParentBlockRoot != block.ParentRoot)
            {
                return Verdict.Ignore(GossipDropReason.InvalidField);
            }

            blobCount = (ulong)(bid.BlobKzgCommitments?.Length ?? 0);
        }
        else
        {
            blobCount = (ulong)(((ForkedSignedBeaconBlock.OfFulu)block).Block.Message!.Body!.BlobKzgCommitments?.Length ?? 0);
        }

        if (SignedBeaconBlockCodec.IsGloasSlot(slot, spec) != gloasTopic)
        {
            return Verdict.Ignore(GossipDropReason.InvalidField);
        }

        return blobCount > (spec.GetBlobParameters(spec.GetEpoch(slot))?.MaxBlobsPerBlock ?? spec.MaxBlobsPerBlockElectra)
            ? Verdict.Ignore(GossipDropReason.LimitExceeded)
            : timing;
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

    // validate_beacon_aggregate_and_proof_gossip: the index and committee-bit REJECTs come first; the target-epoch and
    // participant REJECTs follow store checks, so their failure only drops the message.
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

        return data.Target!.Epoch != epoch || CountSetBits(aggregationBits) == 0 ? Verdict.Ignore(GossipDropReason.InvalidField) : timing;
    }

    // phase0 attester_slashing: an empty intersection means every index is already seen; the slashable-data rule needs no state.
    private static Verdict? ValidateAttesterSlashing(ulong[] indices1, AttestationData data1, ulong[] indices2, AttestationData data2)
    {
        HashSet<ulong> second = [.. indices2];
        bool intersects = false;
        foreach (ulong index in indices1)
        {
            if (second.Contains(index))
            {
                intersects = true;
                break;
            }
        }

        if (!intersects)
        {
            return Verdict.Ignore(GossipDropReason.InvalidField);
        }

        return BeaconStateAccessors.IsSlashableAttestationData(data1, data2) ? null : Verdict.Reject(GossipDropReason.InvalidField);
    }

    // verify_execution_requests_limits MAY run at deserialization (gloas/p2p-interface.md), so it rejects first; the
    // withdrawal-count REJECT follows the block checks, so its failure only drops the message.
    private Verdict? ValidateEnvelope(SignedExecutionPayloadEnvelope envelope)
    {
        ExecutionPayloadEnvelope message = envelope.Message!;
        try
        {
            GloasBlockProcessing.VerifyExecutionRequestsLimits(message.ExecutionRequests ?? new ExecutionRequestsGloas());
        }
        catch (BeaconStateException)
        {
            return Verdict.Reject(GossipDropReason.LimitExceeded);
        }

        ExecutionPayloadGloas payload = message.Payload!;
        if (payload.SlotNumber < FinalizedStartSlot)
        {
            return Verdict.Ignore(GossipDropReason.BeforeFinalized);
        }

        return (payload.Withdrawals?.Length ?? 0) > Presets.MaxWithdrawalsPerPayload ? Verdict.Ignore(GossipDropReason.LimitExceeded) : null;
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

    private readonly record struct Verdict(GossipDropReason Reason, MessageValidity Validity, ulong? DeferToSlot = null)
    {
        public static Verdict Reject(GossipDropReason reason) => new(reason, MessageValidity.Rejected);

        public static Verdict Ignore(GossipDropReason reason) => new(reason, MessageValidity.Ignored);

        public static Verdict DeferTo(ulong slot) => new(GossipDropReason.FutureSlot, MessageValidity.Ignored, slot);
    }
}
