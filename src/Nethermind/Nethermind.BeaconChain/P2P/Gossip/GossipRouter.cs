// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
}

/// <summary>
/// Subscribes the eth2 gossip topics for a fork digest and raises typed events for messages that
/// pass decode-only validation.
/// </summary>
/// <remarks>
/// Validation here is intentionally limited to what needs no beacon state: snappy decompression
/// within <see cref="Eth2MessageId.MaxGossipSize"/>, SSZ decoding, duplicate suppression, and slot
/// sanity against the wall clock (not from the future beyond
/// <see cref="MaximumGossipClockDisparityMs"/>; blocks additionally not older than one epoch).
/// Full spec gossip validation - proposer signature and shuffling, first-block-per-slot,
/// parent-block checks, aggregator selection - requires the head state and belongs to the
/// orchestrator import pipeline consuming these events.
/// </remarks>
public sealed class GossipRouter(BeaconChainSpec spec, SlotClock slotClock, ILogManager logManager, ExecutionPayloadEnvelopePool? envelopePool = null)
{
    /// <summary>The spec <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c>.</summary>
    public const long MaximumGossipClockDisparityMs = 500;

    private const int SeenCacheSize = 2048;

    private readonly ILogger _logger = logManager.GetClassLogger<GossipRouter>();
    private readonly LruKeyCache<ValueHash256> _seenMessages = new(SeenCacheSize, "beacon gossip seen messages");
    private readonly long[] _dropCounts = new long[Enum.GetValues<GossipDropReason>().Length];
    private readonly Lock _subscriptionLock = new();
    private readonly List<(ITopic Topic, Action<byte[]> Handler)> _subscriptions = [];

    private Func<string, ITopic>? _getTopic;
    private byte[] _currentForkDigest = [];
    private bool _gloasDigest;
    private bool _gloasActive;

    /// <summary>Raised with the block decoded as the SSZ shape of its topic's fork.</summary>
    public event Action<ForkedSignedBeaconBlock>? BeaconBlockReceived;
    public event Action<SignedAggregateAndProof>? AggregateAndProofReceived;
    public event Action<SignedVoluntaryExit>? VoluntaryExitReceived;
    public event Action<ProposerSlashing>? ProposerSlashingReceived;
    public event Action<AttesterSlashing>? AttesterSlashingReceived;
    public event Action<SignedExecutionPayloadEnvelope>? ExecutionPayloadEnvelopeReceived;
    public event Action<PayloadAttestationMessage>? PayloadAttestationMessageReceived;

    public long GetDropCount(GossipDropReason reason) => Interlocked.Read(ref _dropCounts[(int)reason]);

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
    /// Adds the Gloas-only gossip topics (<c>execution_payload</c>, <c>payload_attestation_message</c>)
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

            if (_logger.IsInfo) _logger.Info("Activated Gloas beacon gossip topics (execution_payload, payload_attestation_message)");
        }
    }

    /// <summary>The raw-payload handler for an eth2 gossip topic name.</summary>
    public Action<byte[]> HandlerFor(string name) => name switch
    {
        GossipTopics.BeaconBlock => BeaconBlockHandler(_gloasDigest),
        GossipTopics.BeaconAggregateAndProof => HandleAggregateAndProof,
        GossipTopics.VoluntaryExit => HandleVoluntaryExit,
        GossipTopics.ProposerSlashing => HandleProposerSlashing,
        GossipTopics.AttesterSlashing => HandleAttesterSlashing,
        GossipTopics.ExecutionPayload => HandleExecutionPayloadEnvelope,
        GossipTopics.PayloadAttestationMessage => HandlePayloadAttestationMessage,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown gossip topic name"),
    };

    /// <summary>Handles a <c>beacon_block</c> message on the currently subscribed digest's topic.</summary>
    public void HandleBeaconBlock(byte[] message) => HandleBeaconBlock(message, _gloasDigest);

    // The fork is bound at subscription, so a message still in flight on a rotated-out topic keeps that topic's type.
    private Action<byte[]> BeaconBlockHandler(bool gloasTopic) => message => HandleBeaconBlock(message, gloasTopic);

    private void HandleBeaconBlock(byte[] message, bool gloasTopic) =>
        Handle(GossipTopics.BeaconBlock, message,
            payload => SignedBeaconBlockCodec.Decode(payload, spec),
            block => ValidateBlockFork(block, gloasTopic) ?? ValidateBlockSlot(block),
            block => BeaconBlockReceived?.Invoke(block));

    public void HandleAggregateAndProof(byte[] message) =>
        Handle(GossipTopics.BeaconAggregateAndProof, message,
            static payload => { SignedAggregateAndProof.Decode(payload, out SignedAggregateAndProof aggregate); return aggregate; },
            aggregate => ValidateNotFromFuture(aggregate.Message!.Aggregate!.Data!.Slot),
            aggregate => AggregateAndProofReceived?.Invoke(aggregate));

    public void HandleVoluntaryExit(byte[] message) =>
        Handle(GossipTopics.VoluntaryExit, message,
            static payload => { SignedVoluntaryExit.Decode(payload, out SignedVoluntaryExit exit); return exit; },
            validate: null,
            exit => VoluntaryExitReceived?.Invoke(exit));

    public void HandleProposerSlashing(byte[] message) =>
        Handle(GossipTopics.ProposerSlashing, message,
            static payload => { ProposerSlashing.Decode(payload, out ProposerSlashing slashing); return slashing; },
            validate: null,
            slashing => ProposerSlashingReceived?.Invoke(slashing));

    public void HandleAttesterSlashing(byte[] message) =>
        Handle(GossipTopics.AttesterSlashing, message,
            static payload => { AttesterSlashing.Decode(payload, out AttesterSlashing slashing); return slashing; },
            validate: null,
            slashing => AttesterSlashingReceived?.Invoke(slashing));

    /// <summary>
    /// Decode-only: the full <c>execution_payload</c> gossip conditions (envelope's block passes
    /// validation, builder/block-hash/execution-requests-root match the committed bid, envelope
    /// signature) all need the head state and the block's bid, so - like <see cref="HandleBeaconBlock"/> -
    /// they are left to the orchestrator import pipeline.
    /// </summary>
    public void HandleExecutionPayloadEnvelope(byte[] message) =>
        Handle(GossipTopics.ExecutionPayload, message,
            static payload => { SignedExecutionPayloadEnvelope.Decode(payload, out SignedExecutionPayloadEnvelope envelope); return envelope; },
            validate: null,
            envelope =>
            {
                if (envelope.Message is { BeaconBlockRoot: { } root, Payload: { } payload })
                {
                    envelopePool?.Add(root, payload.SlotNumber, envelope);
                }

                ExecutionPayloadEnvelopeReceived?.Invoke(envelope);
            });

    /// <summary>
    /// Decode-only plus the slot-disparity check: validator-index and PTC-membership checks need the
    /// head state and are left to the orchestrator import pipeline, as with the other gossip types here.
    /// </summary>
    public void HandlePayloadAttestationMessage(byte[] message) =>
        Handle(GossipTopics.PayloadAttestationMessage, message,
            static payload => { PayloadAttestationMessage.Decode(payload, out PayloadAttestationMessage attestation); return attestation; },
            attestation => ValidateNotFromFuture(attestation.Data!.Slot),
            attestation => PayloadAttestationMessageReceived?.Invoke(attestation));

    private void Handle<T>(string name, byte[] message, Func<byte[], T> decode, Func<T, GossipDropReason?>? validate, Action<T> raise) where T : class
    {
        Metrics.BeaconChainGossipReceivedByTopic.Increment(new StringLabel(name));

        SnappyDecodeResult snappy = Eth2MessageId.TryDecompress(message, Eth2MessageId.MaxGossipSize, out byte[]? payload);
        if (snappy != SnappyDecodeResult.Decoded)
        {
            Drop(name, snappy == SnappyDecodeResult.Oversized ? GossipDropReason.Oversized : GossipDropReason.InvalidSnappy);
            return;
        }

        ValueHash256 seenKey = SeenKey(name, payload!);
        if (_seenMessages.Get(seenKey))
        {
            Drop(name, GossipDropReason.Duplicate);
            return;
        }

        T value;
        GossipDropReason? invalid;
        try
        {
            value = decode(payload!);
            invalid = validate?.Invoke(value);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Malformed {name} gossip message: {e.Message}");
            Drop(name, GossipDropReason.InvalidSsz);
            return;
        }

        if (invalid is { } reason)
        {
            Drop(name, reason);
            return;
        }

        // Messages are only marked as seen once accepted, so a message dropped for slot sanity can
        // still be delivered by a later copy. Set returns false when a concurrent handler raced us.
        if (!_seenMessages.Set(seenKey))
        {
            Drop(name, GossipDropReason.Duplicate);
            return;
        }

        Metrics.BeaconChainGossipAccepted++;
        raise(value);
    }

    // The topic's fork fixes the message type (phase0 p2p: MUST reject messages containing an incorrect type).
    private static GossipDropReason? ValidateBlockFork(ForkedSignedBeaconBlock block, bool gloasTopic) =>
        (block is ForkedSignedBeaconBlock.OfGloas) == gloasTopic ? null : GossipDropReason.InvalidSsz;

    private GossipDropReason? ValidateBlockSlot(ForkedSignedBeaconBlock block)
    {
        ulong slot = block.Slot;
        if (ValidateNotFromFuture(slot) is { } future)
        {
            return future;
        }

        ulong currentSlot = slotClock.CurrentSlot;
        return currentSlot > spec.SlotsPerEpoch && slot < currentSlot - spec.SlotsPerEpoch ? GossipDropReason.StaleSlot : null;
    }

    private GossipDropReason? ValidateNotFromFuture(ulong slot)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        if (slot <= currentSlot)
        {
            return null;
        }

        // The only tolerated future slot is the next one within MAXIMUM_GOSSIP_CLOCK_DISPARITY of its start.
        return slot == currentSlot + 1 && slotClock.MillisecondsToNextSlot <= MaximumGossipClockDisparityMs
            ? null
            : GossipDropReason.FutureSlot;
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

    private void Drop(string name, GossipDropReason reason)
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
}
