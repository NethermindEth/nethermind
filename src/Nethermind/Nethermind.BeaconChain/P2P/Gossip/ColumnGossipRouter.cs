// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using Snappier;

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

    /// <summary>A Gloas sidecar whose slot is not its block's slot.</summary>
    SlotMismatch,

    /// <summary>A Gloas sidecar whose block this node does not hold as a Gloas block; a well-formed one is kept as a pending candidate.</summary>
    UnknownBlock,

    /// <summary>A slot at or below the finalized checkpoint's start slot.</summary>
    BeforeFinalized,

    /// <summary>A sidecar on a subnet this node has not subscribed.</summary>
    UnsubscribedSubnet,
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
/// proposer's sidecar. A Fulu sidecar is therefore consumed and returned as <see cref="MessageValidity.Ignored"/>, never forwarded.
/// <para>
/// A Gloas sidecar (gloas/p2p-interface.md) needs no state: its block is read from <paramref name="store"/>, which holds only
/// blocks fork choice accepted, so a held block stands in for both "seen" and "passes validation". A sidecar that verifies
/// against the block's bid is stored in <paramref name="pool"/> and returned as <see cref="MessageValidity.Accepted"/>, because
/// the spec requires a valid sidecar to be re-broadcast. A sidecar whose block is not held is parked as a pending candidate.
/// </para>
/// </remarks>
/// <param name="store">Where a Gloas sidecar's block is read from; <c>null</c> holds no block, so every Gloas sidecar is parked.</param>
/// <param name="status">Where the finalized checkpoint is read from; <c>null</c> applies no finalized-slot rule.</param>
public sealed class ColumnGossipRouter(
    BeaconChainSpec spec,
    SlotClock slotClock,
    ILogManager logManager,
    DataColumnSidecarPool? pool = null,
    BeaconChainStore? store = null,
    IBeaconChainStatusSource? status = null)
{
    private const int SeenCacheSize = 4096;
    private const int GloasBlockCacheSize = 64;

    private readonly ILogger _logger = logManager.GetClassLogger<ColumnGossipRouter>();
    private readonly LruKeyCache<(ulong Slot, ulong ProposerIndex, ulong Index)> _seenSidecars = new(SeenCacheSize, "beacon column gossip seen sidecars");
    private readonly LruKeyCache<(Hash256 BlockRoot, ulong Index)> _seenGloasSidecars = new(SeenCacheSize, "beacon column gossip seen gloas sidecars");

    // Every column of a block reads the same bid, so a block is decoded from the store once, not once per column.
    private readonly LruCache<Hash256, GloasBlockColumns> _gloasBlocks = new(GloasBlockCacheSize, "beacon column gossip gloas blocks");

    // gloas/p2p-interface.md compute_max_data_column_sidecar_size: the progressive lists carry no SSZ bound of their own.
    private readonly int _maxGloasSidecarSize = (int)Math.Min(DataColumnSidecarGloasSize.ComputeMax(spec), (ulong)Eth2MessageId.MaxGossipSize);
    private readonly long[] _dropCounts = new long[Enum.GetValues<ColumnGossipDropReason>().Length];
    private readonly Lock _subscriptionLock = new();
    private readonly List<(ulong Subnet, ITopic Topic, Action<byte[]> Handler)> _subscriptions = [];

    // Per-block-root accumulation of held columns, purely to decide when to attempt reconstruction
    // (das-core.md "SHOULD reconstruct" at 50%+); keyed by the full header's hash tree root rather
    // than (slot, proposer_index) so, like DataColumnReconstruction itself, columns of two different
    // blocks can never accumulate together under one key.
    private readonly LruCache<Hash256, List<DataColumnSidecar>> _heldColumnsByBlockRoot = new(SeenCacheSize, "beacon column reconstruction held columns");
    private readonly LruKeyCache<Hash256> _reconstructedBlockRoots = new(SeenCacheSize, "beacon column reconstruction completed blocks");
    private readonly Lock _reconstructionLock = new();

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

            foreach ((ulong _, ITopic topic, Action<byte[]> handler) in _subscriptions)
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
        // The fork is bound at subscription, so a message still in flight on a rotated-out topic keeps that topic's type.
        bool gloasTopic = IsGloasDigest(forkDigest);
        foreach (ulong subnetId in _subnets)
        {
            ITopic topic = _getTopic!(GossipTopics.Topic(forkDigest, GossipTopics.DataColumnSidecarTopicName(subnetId)));
            Action<byte[]> handler = message => Handle(subnetId, gloasTopic, message);
            topic.OnMessage += handler;
            topic.Subscribe();
            _subscriptions.Add((subnetId, topic, handler));
        }

        if (_logger.IsInfo) _logger.Info($"Subscribed {_subnets.Count} data column sidecar subnets for fork digest 0x{Convert.ToHexStringLower(forkDigest)}");
    }

    /// <summary>Validates a raw message from the <c>data_column_sidecar_{subnet_id}</c> topic of a Fulu or Gloas digest and consumes it when it passes.</summary>
    /// <returns>
    /// <see cref="MessageValidity.Accepted"/> only for a Gloas sidecar that verified against its block's bid; otherwise
    /// <see cref="MessageValidity.Rejected"/> or <see cref="MessageValidity.Ignored"/>, including for a consumed Fulu sidecar.
    /// </returns>
    internal MessageValidity Handle(ulong subnetId, bool gloasTopic, byte[] message)
    {
        if (!IsSubscribed(subnetId))
        {
            return Drop(ColumnGossipDropReason.UnsubscribedSubnet, MessageValidity.Ignored);
        }

        SnappyDecodeResult snappy = Eth2MessageId.TryDecompress(message, gloasTopic ? _maxGloasSidecarSize : Eth2MessageId.MaxGossipSize, out byte[]? payload);
        if (snappy != SnappyDecodeResult.Decoded)
        {
            return Drop(snappy == SnappyDecodeResult.Oversized ? ColumnGossipDropReason.Oversized : ColumnGossipDropReason.InvalidSnappy, MessageValidity.Rejected);
        }

        return gloasTopic ? HandleGloas(subnetId, payload!) : HandleFulu(subnetId, payload!);
    }

    private MessageValidity HandleFulu(ulong subnetId, byte[] payload)
    {
        DataColumnSidecar sidecar;
        try
        {
            DataColumnSidecar.Decode(payload!, out sidecar);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Malformed data column sidecar gossip message: {e.Message}");
            return Drop(ColumnGossipDropReason.InvalidSsz, MessageValidity.Rejected);
        }

        // SSZ containers populate every field on decode, so a wire-decoded sidecar's header is never
        // null; this only guards a sidecar reaching this path some other way (e.g. constructed
        // in-process) from null-referencing every check below that reads it.
        if (sidecar.SignedBlockHeader?.Message is not { } header)
        {
            return Drop(ColumnGossipDropReason.FailedStructure, MessageValidity.Rejected);
        }

        (ulong slot, ulong proposerIndex) = (header.Slot, header.ProposerIndex);

        // [IGNORE] first sidecar for the tuple (slot, proposer_index, index).
        if (_seenSidecars.Get((slot, proposerIndex, sidecar.Index)))
        {
            return Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);
        }

        // [REJECT] structural part of verify_data_column_sidecar: index range, non-empty, matching lengths.
        if (!DataColumnSidecarVerifier.VerifyStructure(sidecar))
        {
            return Drop(ColumnGossipDropReason.FailedStructure, MessageValidity.Rejected);
        }

        // [REJECT] the commitment count is within the max_blobs_per_block scheduled for this
        // sidecar's own epoch, which a blob-parameter-only fork raises without a version bump.
        if (!DataColumnSidecarVerifier.VerifyBlobCount(sidecar, spec))
        {
            return Drop(ColumnGossipDropReason.FailedBlobCount, MessageValidity.Rejected);
        }

        // [REJECT] the sidecar is for the correct subnet.
        if (CustodyGroups.ComputeSubnetForDataColumnSidecar(sidecar.Index) != subnetId)
        {
            return Drop(ColumnGossipDropReason.WrongSubnet, MessageValidity.Rejected);
        }

        // [IGNORE] not from a future slot, and from a slot greater than the latest finalized slot. Like
        // GossipRouter.ValidateBlockSlot does for blocks, anything more than one epoch stale by the
        // wall clock is also dropped.
        ColumnGossipDropReason? future = ValidateNotFromFuture(slot);
        if (future is { } futureReason)
        {
            return Drop(futureReason, MessageValidity.Ignored);
        }

        if (status is not null && slot <= BeaconStateAccessors.ComputeStartSlotAtEpoch(status.CurrentStatus.FinalizedEpoch))
        {
            return Drop(ColumnGossipDropReason.BeforeFinalized, MessageValidity.Ignored);
        }

        ulong currentSlot = slotClock.CurrentSlot;
        if (currentSlot > spec.SlotsPerEpoch && slot < currentSlot - spec.SlotsPerEpoch)
        {
            return Drop(ColumnGossipDropReason.StaleSlot, MessageValidity.Ignored);
        }

        // [REJECT] verify_data_column_sidecar_inclusion_proof: a handful of SHA256 hashes, cheap. It and the KZG check are
        // ordered after the parent checks, which need state, so a failure here is only Ignored.
        if (!DataColumnSidecarVerifier.VerifyInclusionProof(sidecar))
        {
            return Drop(ColumnGossipDropReason.FailedInclusionProof, MessageValidity.Ignored);
        }

        // [REJECT] verify_data_column_sidecar_kzg_proofs: a native KZG cell-proof batch verification
        // (Ckzg.VerifyCellKzgProofBatch) over up to MaxBlobCommitmentsPerBlock cells, run synchronously
        // on this call stack. Unlike every check above, this is real cryptographic work rather than a
        // hash or a field comparison; the pubsub library invokes this validator inline on its message
        // read loop, so this call blocks that loop for however long the native batch verify takes
        // (not benchmarked here - it scales with the sidecar's commitment count, unlike the O(1) checks
        // above). The seen-check above keys on slot and proposer index from this same unverified
        // header, plus the column index from the sidecar itself; nothing this router checks ties any
        // of them to the real proposer. An attacker who mints their own header therefore controls the
        // key, so this cost is paid once per distinct key the attacker chooses to send, not once per
        // real sidecar - the deferred proposer signature check is what would actually bound it.
        if (!DataColumnSidecarVerifier.VerifyKzgProofs(sidecar))
        {
            return Drop(ColumnGossipDropReason.FailedKzgProofs, MessageValidity.Ignored);
        }

        // Deferred (need beacon state / fork choice, exactly like GossipRouter's block handling defers
        // proposer signature and shuffling): parent-seen, parent-passes-validation, proposer index
        // validity and signature, higher-slot-than-parent, finalized-checkpoint-is-ancestor, and
        // expected-proposer. A caller must run those before trusting this sidecar against an
        // adversarial proposer.
        if (!_seenSidecars.Set((slot, proposerIndex, sidecar.Index)))
        {
            return Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);
        }

        Hash256 blockRoot = SszRoots.HashTreeRoot(header);
        pool?.Add(blockRoot, slot, sidecar);
        DataColumnSidecarReceived?.Invoke(sidecar);

        TrackHeldColumnAndMaybeReconstruct(blockRoot, sidecar);
        return MessageValidity.Ignored;
    }

    /// <summary>gloas/p2p-interface.md <c>validate_data_column_sidecar_gossip</c>, in the spec's order.</summary>
    private MessageValidity HandleGloas(ulong subnetId, byte[] payload)
    {
        DataColumnSidecarGloas sidecar;
        try
        {
            DataColumnSidecarGloas.Decode(payload, out sidecar);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Malformed Gloas data column sidecar gossip message: {e.Message}");
            return Drop(ColumnGossipDropReason.InvalidSsz, MessageValidity.Rejected);
        }

        if (sidecar.BeaconBlockRoot is not { } blockRoot)
        {
            return Drop(ColumnGossipDropReason.FailedStructure, MessageValidity.Rejected);
        }

        // [IGNORE] first sidecar for the tuple (beacon_block_root, index).
        if (_seenGloasSidecars.Get((blockRoot, sidecar.Index)))
        {
            return Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);
        }

        // [REJECT] the sidecar is for the correct subnet.
        if (CustodyGroups.ComputeSubnetForDataColumnSidecar(sidecar.Index) != subnetId)
        {
            return Drop(ColumnGossipDropReason.WrongSubnet, MessageValidity.Rejected);
        }

        // [IGNORE] not from a future slot. One early for the next slot is parked: its message id stays dropped for the seen TTL.
        if (ValidateNotFromFuture(sidecar.Slot) is { } futureReason)
        {
            return sidecar.Slot == slotClock.CurrentSlot + 1
                ? Park(sidecar, payload, futureReason)
                : Drop(futureReason, MessageValidity.Ignored);
        }

        // [IGNORE] the block has been seen, and [REJECT] it passes validation: the store holds only blocks fork choice accepted.
        switch (TryGetGloasBlock(blockRoot, out GloasBlockColumns block))
        {
            case GloasBlockLookup.Unknown:
                return Park(sidecar, payload, ColumnGossipDropReason.UnknownBlock);
            case GloasBlockLookup.NotGloas:
                return Drop(ColumnGossipDropReason.UnknownBlock, MessageValidity.Ignored);
        }

        // [REJECT] the sidecar's slot matches the slot of the block.
        if (sidecar.Slot != block.Slot)
        {
            return Drop(ColumnGossipDropReason.SlotMismatch, MessageValidity.Rejected);
        }

        // An accepted block's bid is within max_blobs_per_block; a stored bid over it cannot convict the sidecar, only bound the KZG work.
        if ((ulong)block.Commitments.Length > MaxBlobsPerBlock(block.Slot))
        {
            return Drop(ColumnGossipDropReason.FailedBlobCount, MessageValidity.Ignored);
        }

        // [REJECT] verify_data_column_sidecar against the bid's commitments; it bounds the KZG batch below by the bid.
        if (!DataColumnSidecarVerifier.VerifyStructure(sidecar, block.Commitments))
        {
            return Drop(ColumnGossipDropReason.FailedStructure, MessageValidity.Rejected);
        }

        // [REJECT] verify_data_column_sidecar_kzg_proofs against the bid's commitments.
        if (!DataColumnSidecarVerifier.VerifyKzgProofs(sidecar, block.Commitments))
        {
            return Drop(ColumnGossipDropReason.FailedKzgProofs, MessageValidity.Rejected);
        }

        if (!_seenGloasSidecars.Set((blockRoot, sidecar.Index)))
        {
            return Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);
        }

        pool?.AddGloas(sidecar);
        return MessageValidity.Accepted;
    }

    /// <summary>
    /// Keeps a sidecar whose block is not yet held as a pending candidate (gloas/p2p-interface.md "MAY be queued until
    /// block is retrieved"), once it passes the checks that bound what an unverified candidate can hold.
    /// </summary>
    /// <remarks>
    /// The pubsub validator is not told which peer delivered a message, and <c>StrictNoSign</c> requires the message's
    /// <c>from</c> to be absent, so a candidate's source key is the hash of its encoding: a forgery is always a separate
    /// candidate and never replaces another one.
    /// </remarks>
    private MessageValidity Park(DataColumnSidecarGloas sidecar, byte[] payload, ColumnGossipDropReason reason)
    {
        if (sidecar.Index >= (ulong)Eip7594DasConstants.NumberOfColumns
            || sidecar.Column is not { Length: > 0 } column
            || sidecar.KzgProofs?.Length != column.Length)
        {
            return Drop(ColumnGossipDropReason.FailedStructure, MessageValidity.Ignored);
        }

        // A bid never commits more than max_blobs_per_block, so a longer column can never verify against one.
        if ((ulong)column.Length > MaxBlobsPerBlock(sidecar.Slot))
        {
            return Drop(ColumnGossipDropReason.FailedBlobCount, MessageValidity.Ignored);
        }

        pool?.AddPendingGloas(sidecar, Convert.ToHexString(SHA256.HashData(payload)));
        return Drop(reason, MessageValidity.Ignored);
    }

    private GloasBlockLookup TryGetGloasBlock(Hash256 blockRoot, out GloasBlockColumns block)
    {
        if (_gloasBlocks.TryGet(blockRoot, out block))
        {
            return GloasBlockLookup.Found;
        }

        ForkedSignedBeaconBlock? forked;
        try
        {
            if (store is null || !store.TryGetForkedBlock(blockRoot, out forked))
            {
                return GloasBlockLookup.Unknown;
            }
        }
        catch (Exception e) when (e is BeaconStateException or InvalidDataException)
        {
            if (_logger.IsWarn) _logger.Warn($"Unreadable stored block {blockRoot} named by a data column sidecar: {e.Message}");
            return GloasBlockLookup.NotGloas;
        }

        if (forked is not ForkedSignedBeaconBlock.OfGloas { Block.Message: { } message })
        {
            return GloasBlockLookup.NotGloas;
        }

        block = new GloasBlockColumns(message.Slot, message.Body?.SignedExecutionPayloadBid?.Message?.BlobKzgCommitments ?? []);
        _gloasBlocks.Set(blockRoot, block);
        return GloasBlockLookup.Found;
    }

    private ulong MaxBlobsPerBlock(ulong slot) =>
        spec.GetBlobParameters(spec.GetEpoch(slot))?.MaxBlobsPerBlock ?? spec.MaxBlobsPerBlockElectra;

    private bool IsSubscribed(ulong subnetId)
    {
        foreach (ulong subscribed in _subnets)
        {
            if (subscribed == subnetId)
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>
    /// Accumulates <paramref name="sidecar"/> under <paramref name="blockRoot"/> and, once this
    /// block's held columns cross <see cref="Eip7594DasConstants.RequiredColumnsForReconstruction"/>,
    /// reconstructs the full matrix and publishes the columns this node did not itself receive
    /// (Fulu p2p-interface.md "distributed blob publishing"). Runs at most once per block: a
    /// completed root is never revisited, so a later gossip arrival for the same block cannot
    /// re-reconstruct or re-publish. Different subnets' <c>OnMessage</c> callbacks can fire
    /// concurrently on separate threads for the very columns reconstruction watches, so the
    /// read-check-mutate sequence over <see cref="_heldColumnsByBlockRoot"/> and
    /// <see cref="_reconstructedBlockRoots"/> runs under <see cref="_reconstructionLock"/>: each
    /// cache is individually thread-safe, but that does not make Get-then-Add-then-Set atomic, and
    /// an unguarded race here silently drops held columns rather than merely delaying reconstruction.
    /// </summary>
    private void TrackHeldColumnAndMaybeReconstruct(Hash256 blockRoot, DataColumnSidecar sidecar)
    {
        List<DataColumnSidecar> held;
        DataColumnSidecar[] fullMatrix;
        lock (_reconstructionLock)
        {
            if (_reconstructedBlockRoots.Get(blockRoot))
            {
                return;
            }

            held = _heldColumnsByBlockRoot.Get(blockRoot) ?? [];
            held.Add(sidecar);
            _heldColumnsByBlockRoot.Set(blockRoot, held);

            if (held.Count < Eip7594DasConstants.RequiredColumnsForReconstruction
                || !DataColumnReconstruction.TryReconstruct(held, out fullMatrix))
            {
                return;
            }

            _reconstructedBlockRoots.Set(blockRoot);
            _heldColumnsByBlockRoot.Delete(blockRoot);
        }

        foreach (ReconstructedSidecarToPublish entry in ReconstructionBroadcast.SelectNewlyReconstructed(held, fullMatrix))
        {
            PublishReconstructed(entry);
        }
    }

    /// <summary>
    /// Exposes a locally reconstructed sidecar exactly as if it had arrived over gossip: marks the
    /// anti-equivocation cache first, then adds it to the serving pool, raises
    /// <see cref="DataColumnSidecarReceived"/>, and - only if this node is subscribed to the
    /// sidecar's own subnet - publishes it there. Marking first (rather than after publishing) means
    /// a genuine concurrent gossip arrival for the same (slot, proposer_index, index) is correctly
    /// caught as a duplicate by <see cref="Handle"/> instead of racing this method's own update.
    /// </summary>
    private void PublishReconstructed(ReconstructedSidecarToPublish entry)
    {
        if (!_seenSidecars.Set((entry.Slot, entry.ProposerIndex, entry.Sidecar.Index)))
        {
            // A gossip copy of this exact column won the race and already marked the cache: its own
            // Handle call already added it to the pool and raised the event, so do neither again.
            return;
        }

        Hash256 blockRoot = SszRoots.HashTreeRoot(entry.Sidecar.SignedBlockHeader!.Message!);
        pool?.Add(blockRoot, entry.Slot, entry.Sidecar);
        DataColumnSidecarReceived?.Invoke(entry.Sidecar);

        if (FindSubnetTopic(entry.Subnet) is { } topic)
        {
            topic.Publish(Snappy.CompressToArray(DataColumnSidecar.Encode(entry.Sidecar)));
        }
    }

    /// <summary>The subscribed topic for <paramref name="subnet"/>, or null if this node does not custody it.</summary>
    private ITopic? FindSubnetTopic(ulong subnet)
    {
        lock (_subscriptionLock)
        {
            foreach ((ulong subscribedSubnet, ITopic subscribedTopic, _) in _subscriptions)
            {
                if (subscribedSubnet == subnet)
                {
                    return subscribedTopic;
                }
            }
        }

        return null;
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

    private MessageValidity Drop(ColumnGossipDropReason reason, MessageValidity validity)
    {
        Interlocked.Increment(ref _dropCounts[(int)reason]);
        if (_logger.IsTrace) _logger.Trace($"Dropped data column sidecar gossip message: {reason}");
        return validity;
    }

    private readonly record struct GloasBlockColumns(ulong Slot, SszKzgCommitment[] Commitments);

    private enum GloasBlockLookup
    {
        Found,
        Unknown,
        NotGloas,
    }
}
