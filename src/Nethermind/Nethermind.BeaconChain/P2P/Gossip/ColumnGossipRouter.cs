// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Why a <c>data_column_sidecar_{subnet_id}</c> gossip message was dropped.</summary>
public enum ColumnGossipDropReason
{
    Oversized,
    InvalidSnappy,
    InvalidSsz,
    WrongSubnet,
    FutureSlot,
    StaleSlot,
    Duplicate,
    FailedStructure,
    FailedBlobCount,
    FailedInclusionProof,
    FailedKzgProofs,
}

/// <summary>
/// Subscribes this node's custodied <c>data_column_sidecar_{subnet_id}</c> gossip topics and raises a
/// typed event for sidecars that pass the validation this router can do without beacon state.
/// </summary>
/// <remarks>
/// Implements the EIP-7594 <c>validate_data_column_sidecar_gossip</c> conditions in the spec's own
/// order (p2p-interface.md), cheapest structural checks first: decode, per-(slot, proposer_index,
/// index) duplicate suppression, the structural part of <c>verify_data_column_sidecar</c>, subnet
/// correctness, not-from-a-future-slot, then the two cryptographic checks (merkle inclusion proof,
/// KZG cell-proof batch). The remaining conditions - the sidecar's block parent has been seen and
/// passes validation, the proposer index is valid and its signature verifies, the sidecar's slot is
/// higher than its parent's, the current finalized checkpoint is an ancestor of the sidecar's block,
/// and the sidecar is proposed by the expected proposer - all need the head state and fork choice.
/// Exactly like <see cref="GossipRouter"/> leaves full block validation to the orchestrator import
/// pipeline, this router does not implement them: <see cref="DataColumnSidecarReceived"/> firing is
/// not spec-complete acceptance, and a caller must run those checks before trusting an adversarial
/// proposer's sidecar.
/// </remarks>
public sealed class ColumnGossipRouter(BeaconChainSpec spec, SlotClock slotClock, ILogManager logManager, DataColumnSidecarPool? pool = null)
{
    private const int SeenCacheSize = 4096;

    private readonly ILogger _logger = logManager.GetClassLogger<ColumnGossipRouter>();
    private readonly LruKeyCache<(ulong Slot, ulong ProposerIndex, ulong Index)> _seenSidecars = new(SeenCacheSize, "beacon column gossip seen sidecars");
    private readonly long[] _dropCounts = new long[Enum.GetValues<ColumnGossipDropReason>().Length];
    private readonly Lock _subscriptionLock = new();
    private readonly List<(ITopic Topic, Action<byte[]> Handler)> _subscriptions = [];

    private Func<string, ITopic>? _getTopic;
    private byte[] _currentForkDigest = [];
    private IReadOnlyList<ulong> _subnets = [];

    /// <summary>
    /// Raised for a sidecar that passed every check this router can do without beacon state (see
    /// remarks) - not full spec acceptance.
    /// </summary>
    public event Action<DataColumnSidecar>? DataColumnSidecarReceived;

    public long GetDropCount(ColumnGossipDropReason reason) => Interlocked.Read(ref _dropCounts[(int)reason]);

    /// <summary>Subscribes <paramref name="subnets"/> (see <see cref="Discovery.LocalCustody.Subnets"/>) for <paramref name="forkDigest"/>.</summary>
    public void Start(Func<string, ITopic> getTopic, byte[] forkDigest, IReadOnlyList<ulong> subnets)
    {
        lock (_subscriptionLock)
        {
            _getTopic = getTopic;
            _subnets = subnets;
            SubscribeSubnets(forkDigest);
        }
    }

    /// <summary>Moves all subnet subscriptions to a new fork digest at a fork activation or EIP-7892 BPO boundary.</summary>
    public void RotateDigest(byte[] newForkDigest)
    {
        lock (_subscriptionLock)
        {
            if (_getTopic is null)
            {
                throw new InvalidOperationException($"{nameof(ColumnGossipRouter)} is not started");
            }

            foreach ((ITopic topic, Action<byte[]> handler) in _subscriptions)
            {
                topic.OnMessage -= handler;
                topic.Unsubscribe();
            }

            _subscriptions.Clear();
            SubscribeSubnets(newForkDigest);
        }
    }

    private void SubscribeSubnets(byte[] forkDigest)
    {
        _currentForkDigest = forkDigest;
        foreach (ulong subnetId in _subnets)
        {
            ITopic topic = _getTopic!(GossipTopics.Topic(forkDigest, GossipTopics.DataColumnSidecarTopicName(subnetId)));
            Action<byte[]> handler = message => Handle(subnetId, message);
            topic.OnMessage += handler;
            topic.Subscribe();
            _subscriptions.Add((topic, handler));
        }

        if (_logger.IsInfo) _logger.Info($"Subscribed {_subnets.Count} data column sidecar subnets for fork digest 0x{Convert.ToHexStringLower(forkDigest)}");
    }

    private void Handle(ulong subnetId, byte[] message)
    {
        SnappyDecodeResult snappy = Eth2MessageId.TryDecompress(message, Eth2MessageId.MaxGossipSize, out byte[]? payload);
        if (snappy != SnappyDecodeResult.Decoded)
        {
            Drop(snappy == SnappyDecodeResult.Oversized ? ColumnGossipDropReason.Oversized : ColumnGossipDropReason.InvalidSnappy);
            return;
        }

        DataColumnSidecar sidecar;
        try
        {
            DataColumnSidecar.Decode(payload!, out sidecar);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Malformed data column sidecar gossip message: {e.Message}");
            Drop(ColumnGossipDropReason.InvalidSsz);
            return;
        }

        // SSZ containers populate every field on decode, so a wire-decoded sidecar's header is never
        // null; this only guards a sidecar reaching this path some other way (e.g. constructed
        // in-process) from null-referencing every check below that reads it.
        if (sidecar.SignedBlockHeader?.Message is not { } header)
        {
            Drop(ColumnGossipDropReason.FailedStructure);
            return;
        }

        (ulong slot, ulong proposerIndex) = (header.Slot, header.ProposerIndex);

        // [IGNORE] first sidecar for the tuple (slot, proposer_index, index).
        if (_seenSidecars.Get((slot, proposerIndex, sidecar.Index)))
        {
            Drop(ColumnGossipDropReason.Duplicate);
            return;
        }

        // [REJECT] structural part of verify_data_column_sidecar: index range, non-empty, matching lengths.
        if (!DataColumnSidecarVerifier.VerifyStructure(sidecar))
        {
            Drop(ColumnGossipDropReason.FailedStructure);
            return;
        }

        // [REJECT] the commitment count is within the max_blobs_per_block scheduled for this
        // sidecar's own epoch, which a blob-parameter-only fork raises without a version bump.
        if (!DataColumnSidecarVerifier.VerifyBlobCount(sidecar, spec))
        {
            Drop(ColumnGossipDropReason.FailedBlobCount);
            return;
        }

        // [REJECT] the sidecar is for the correct subnet.
        if (CustodyGroups.ComputeSubnetForDataColumnSidecar(sidecar.Index) != subnetId)
        {
            Drop(ColumnGossipDropReason.WrongSubnet);
            return;
        }

        // [IGNORE] not from a future slot. The spec's companion condition ("from a slot greater than
        // the latest finalized slot") needs the head state; approximated here, like
        // GossipRouter.ValidateBlockSlot does for blocks, by also rejecting anything more than one
        // epoch stale by the wall clock.
        ColumnGossipDropReason? future = ValidateNotFromFuture(slot);
        if (future is { } futureReason)
        {
            Drop(futureReason);
            return;
        }

        ulong currentSlot = slotClock.CurrentSlot;
        if (currentSlot > spec.SlotsPerEpoch && slot < currentSlot - spec.SlotsPerEpoch)
        {
            Drop(ColumnGossipDropReason.StaleSlot);
            return;
        }

        // [REJECT] verify_data_column_sidecar_inclusion_proof: a handful of SHA256 hashes, cheap.
        if (!DataColumnSidecarVerifier.VerifyInclusionProof(sidecar))
        {
            Drop(ColumnGossipDropReason.FailedInclusionProof);
            return;
        }

        // [REJECT] verify_data_column_sidecar_kzg_proofs: a native KZG cell-proof batch verification
        // (Ckzg.VerifyCellKzgProofBatch) over up to MaxBlobCommitmentsPerBlock cells, run synchronously
        // on this call stack. Unlike every check above, this is real cryptographic work rather than a
        // hash or a field comparison; the pubsub library invokes this validator inline on its message
        // read loop, so this call blocks that loop for however long the native batch verify takes
        // (not benchmarked here - it scales with the sidecar's commitment count, unlike the O(1) checks
        // above).
        if (!DataColumnSidecarVerifier.VerifyKzgProofs(sidecar))
        {
            Drop(ColumnGossipDropReason.FailedKzgProofs);
            return;
        }

        // Deferred (need beacon state / fork choice, exactly like GossipRouter's block handling defers
        // proposer signature and shuffling): parent-seen, parent-passes-validation, proposer index
        // validity and signature, higher-slot-than-parent, finalized-checkpoint-is-ancestor, and
        // expected-proposer. A caller must run those before trusting this sidecar against an
        // adversarial proposer.
        if (!_seenSidecars.Set((slot, proposerIndex, sidecar.Index)))
        {
            Drop(ColumnGossipDropReason.Duplicate);
            return;
        }

        pool?.Add(SszRoots.HashTreeRoot(header), slot, sidecar);
        DataColumnSidecarReceived?.Invoke(sidecar);
    }

    private ColumnGossipDropReason? ValidateNotFromFuture(ulong slot)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        if (slot <= currentSlot)
        {
            return null;
        }

        // The only tolerated future slot is the next one within MAXIMUM_GOSSIP_CLOCK_DISPARITY of its start.
        return slot == currentSlot + 1 && slotClock.MillisecondsToNextSlot <= GossipRouter.MaximumGossipClockDisparityMs
            ? null
            : ColumnGossipDropReason.FutureSlot;
    }

    private void Drop(ColumnGossipDropReason reason)
    {
        Interlocked.Increment(ref _dropCounts[(int)reason]);
        if (_logger.IsTrace) _logger.Trace($"Dropped data column sidecar gossip message: {reason}");
    }
}
