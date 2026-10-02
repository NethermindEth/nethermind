// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Network.Libp2p;
using Snappier;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

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

    /// <summary>A Gloas sidecar whose stored block is not cached and not canonical at a recent slot, after this slot's store decodes are spent.</summary>
    StoreDecodeBudgetSpent,

    /// <summary>A Fulu sidecar whose slot is not above the slot of its held parent.</summary>
    NotAboveParentSlot,

    /// <summary>A Fulu sidecar whose header is an imported block's but whose signature is not the one it was imported with.</summary>
    HeaderSignatureMismatch,

    /// <summary>A Fulu sidecar whose block is not a descendant of the finalized checkpoint.</summary>
    NotFinalizedDescendant,

    /// <summary>A Fulu sidecar whose proposer index has no public key in this node's validator key cache.</summary>
    UnknownProposer,

    /// <summary>A Fulu sidecar whose header signature does not verify against its proposer's public key.</summary>
    InvalidHeaderSignature,

    /// <summary>A Fulu sidecar under another signed header for a (slot, proposer_index) that already signed <see cref="ColumnGossipRouter.SignedHeadersPerProposal"/>.</summary>
    ProposerHeaderLimit,

    /// <summary>A Fulu sidecar whose proposer index is not the one the published proposer lookahead names for its slot on its branch.</summary>
    UnexpectedProposer,

    /// <summary>A Fulu sidecar of a header this node has not imported whose branch or slot the published proposer lookahead does not cover.</summary>
    ProposerNotVerifiable,

    /// <summary>A Fulu sidecar for a (block root, index) that already ran <see cref="ColumnGossipRouter.KzgBatchesPerColumn"/> KZG batches.</summary>
    KzgBatchLimit,

    /// <summary>A Fulu sidecar whose block's parent, or a Gloas sidecar whose block, the importer refused for failing validation.</summary>
    FailedBlockValidation,
}

/// <summary>
/// Subscribes this node's custodied <c>data_column_sidecar_{subnet_id}</c> gossip topics and raises a
/// typed event for sidecars that pass the validation this router can do without beacon state.
/// </summary>
/// <remarks>
/// Implements the fulu/p2p-interface.md <c>data_column_sidecar_{subnet_id}</c> conditions in the spec's
/// order: decode, the structural part of <c>verify_data_column_sidecar</c>, subnet correctness, and the
/// future and finalized slots. A sidecar whose header is a block this node imported (read from
/// <paramref name="store"/>) has the signature and proposer that import verified, so it is checked against the
/// finalized checkpoint, its inclusion proof and its KZG proofs, and is <see cref="MessageValidity.Accepted"/> when
/// all pass. Any other header is checked against the expected proposer in <paramref name="proposerLookahead"/>, then
/// the parent checks fork choice can answer and its proposer signature against <paramref name="pubkeys"/>, all before
/// any KZG work; one whose branch or slot the lookahead does not cover runs no KZG work and, when its signature verified, is
/// queued with its message's verdict until the next snapshot or lookahead is published, else dropped as the spec's IGNORE. A sidecar that passes every check
/// is <see cref="MessageValidity.Accepted"/>. A slot not above a held parent's is rejected first, from the store. KZG work per (block root, index)
/// stops after <see cref="KzgBatchesPerColumn"/> batches, since every column of a block carries the same header.
/// <para>
/// A Gloas sidecar (gloas/p2p-interface.md) needs no state: its block is read from <paramref name="store"/>, which holds only
/// blocks fork choice accepted, so a held block stands in for both "seen" and "passes validation". A sidecar that verifies
/// against the block's bid is stored in <paramref name="pool"/> and returned as <see cref="MessageValidity.Accepted"/>, because
/// the spec requires a valid sidecar to be re-broadcast. A sidecar for the current or next slot whose block is not held is
/// parked as a pending candidate.
/// </para>
/// </remarks>
/// <param name="store">Where a Gloas sidecar's block is read from; <c>null</c> holds no block, so every Gloas sidecar is parked.</param>
/// <param name="status">Where the finalized checkpoint is read from; <c>null</c> applies no finalized-slot rule.</param>
/// <param name="forkChoice">Where a Fulu sidecar's parent and finalized ancestry are read from; <c>null</c> applies neither rule.</param>
/// <param name="pubkeys">Where a Fulu header's proposer public key is read from; <c>null</c> applies no signature rule.</param>
/// <param name="proposerLookahead">
/// Where a Fulu header's expected proposer is read from; <c>null</c> applies no expected-proposer rule, so no sidecar of
/// a header this node has not imported is forwarded.
/// </param>
/// <param name="failedBlocks">Where the roots of blocks the importer refused are read from; <c>null</c> applies no failed-validation rule.</param>
public sealed class ColumnGossipRouter(
    BeaconChainSpec spec,
    SlotClock slotClock,
    ILogManager logManager,
    DataColumnSidecarPool? pool = null,
    BeaconChainStore? store = null,
    IBeaconChainStatusSource? status = null,
    ForkChoiceSnapshotHolder? forkChoice = null,
    PubkeyCache? pubkeys = null,
    ProposerLookaheadHolder? proposerLookahead = null,
    FailedBlockRoots? failedBlocks = null)
{
    private const int SeenCacheSize = 4096;
    internal const int GloasBlockCacheSize = 64;
    internal const int NonGloasBlockCacheSize = 4096;
    private const int ImportedHeaderCacheSize = 256;
    private const int VerifiedColumnCacheSize = 1 << 14;
    private const int HeaderSignatureCacheSize = 1024;

    // Holds every (root, index) that can reach KZG in the slot window, two roots per slot over 34 slots and 128 columns, so eviction never resets a live count.
    private const int KzgBatchCacheSize = 1 << 14;

    // Two blocks' worth of every column; each key holds at most KzgBatchesPerColumn sidecars.
    private const int ParkedColumnCacheSize = 2 * Eip7594DasConstants.NumberOfColumns;

    /// <summary>The most (block root, index) keys one proposer_index may hold in the queue, so a single key holder cannot evict every honest column.</summary>
    internal const int ParkedColumnsPerProposer = ParkedColumnCacheSize / 2;

    /// <summary>The most distinct signed headers a (slot, proposer_index) may run KZG work under.</summary>
    /// <remarks>
    /// The expected proposer of a slot can still sign any number of headers for it. An honest proposer signs one; two
    /// already prove an equivocation, so more only add KZG work chosen by the signer.
    /// </remarks>
    internal const int SignedHeadersPerProposal = 2;

    /// <summary>The most KZG batches run for one (block root, index); later copies are dropped without KZG work.</summary>
    /// <remarks>
    /// Every column of a block carries the same signed header and inclusion proof, so anyone can copy them and send any
    /// number of copies with altered cells, and a REJECT costs only the delivering peer's gossip score (<see cref="GossipScoring"/>),
    /// which a new peer id escapes. An honest copy that arrives within the bound still verifies; a later one is refused.
    /// </remarks>
    internal const int KzgBatchesPerColumn = 2;

    /// <summary>The most stored blocks decoded per slot for sidecars whose block is not cached, has no block summary and is not canonical at a recent slot.</summary>
    /// <remarks>
    /// The slot and bid required by gloas/p2p-interface.md are indexed with new blocks; only legacy records spend this budget.
    /// A REJECT costs only the delivering peer's gossip score, which a new peer id escapes, so it cannot bound decode work.
    /// A sidecar whose root is the canonical block at its own slot, for the current or previous slot, is decoded outside
    /// the budget: that is the honest case, and such a root is decoded once and then cached. A record the store does not
    /// hold costs a key lookup, not a decode, and is still parked. The budget covers the rest, such as a competing block or
    /// one stored but not yet canonical, of which an honest slot has at most a few; once it is spent, such a sidecar is
    /// Ignored until the next slot after a key lookup and a canonical-index read, with no block decoded.
    /// </remarks>
    internal const int StoreDecodesPerSlot = 16;

    /// <summary>The most stored blocks decoded per slot to read the slot of a Fulu sidecar's held, uncached parent.</summary>
    internal const int ParentSlotReadsPerSlot = 16;

    private readonly ILogger _logger = logManager.GetClassLogger<ColumnGossipRouter>();
    private readonly LruKeyCache<(ulong Slot, ulong ProposerIndex, ulong Index)> _seenSidecars = new(SeenCacheSize, "beacon column gossip seen sidecars");
    private readonly LruKeyCache<(Hash256 BlockRoot, ulong Index)> _seenGloasSidecars = new(SeenCacheSize, "beacon column gossip seen gloas sidecars");

    // The signature each stored block was imported with, or null for a stored root that is not a readable Fulu block.
    private readonly LruCache<Hash256, BlsSignature?> _importedHeaderSignatures = new(ImportedHeaderCacheSize, "beacon column gossip imported headers");
    private volatile SnapshotIndex? _snapshotIndex;
    private long _kzgBatches;
    private long _headerSignatureVerifications;

    // Keyed by header root, not by the header's (slot, proposer_index), so a forged header never marks an honest sidecar seen.
    private readonly LruKeyCache<(Hash256 BlockRoot, ulong Index)> _verifiedColumns = new(VerifiedColumnCacheSize, "beacon column gossip verified columns");

    // Every column of a block carries the same header, so its signature is verified once, not once per column.
    private readonly LruCache<Hash256, HeaderSignatureCheck> _headerSignatures = new(HeaderSignatureCacheSize, "beacon column gossip header signatures");
    private readonly LruCache<(ulong Slot, ulong ProposerIndex), Hash256[]> _signedHeaderRoots = new(SeenCacheSize, "beacon column gossip signed headers");
    private readonly Lock _signedHeaderRootsLock = new();
    private readonly LruCache<(Hash256 BlockRoot, ulong Index), int> _kzgBatchesByColumn = new(KzgBatchCacheSize, "beacon column gossip kzg batches");
    private readonly Lock _kzgBatchesLock = new();

    // Signed sidecars whose expected proposer the published snapshot and lookahead cannot verify yet.
    private readonly LruCache<(Hash256 BlockRoot, ulong Index), ParkedColumn[]> _parkedColumns = new(ParkedColumnCacheSize, "beacon column gossip parked columns");
    private readonly Lock _parkedLock = new();
    private ForkChoiceSnapshot? _retriedSnapshot;
    private ProposerLookaheadSnapshot? _retriedLookahead;

    // Every column of a block reads the same bid, so a block is decoded from the store once, not once per column.
    private readonly LruCache<Hash256, GloasBlockColumns> _gloasBlocks = new(GloasBlockCacheSize, "beacon column gossip gloas blocks");

    // A root whose stored block is not a readable Gloas block never becomes one, so it is decoded once, not once per message.
    private readonly LruCache<Hash256, GloasBlockLookup> _nonGloasBlocks = new(NonGloasBlockCacheSize, "beacon column gossip non-gloas blocks");

    private readonly PerSlotBudget _storeDecodes = new(StoreDecodesPerSlot);
    private readonly PerSlotBudget _parentSlotReads = new(ParentSlotReadsPerSlot);
    private readonly StoredBlockSlots? _storedBlockSlots = store is null ? null : new(store, slotClock, logManager.GetClassLogger<ColumnGossipRouter>(), "beacon column gossip parent slots");

    // gloas/p2p-interface.md compute_max_data_column_sidecar_size: the progressive lists carry no SSZ bound of their own.
    private readonly int _maxGloasSidecarSize = (int)Math.Min(DataColumnSidecarGloasSize.ComputeMax(spec), (ulong)Eth2MessageId.MaxGossipSize);
    private readonly long[] _dropCounts = new long[Enum.GetValues<ColumnGossipDropReason>().Length];
    private readonly Lock _subscriptionLock = new();
    private readonly Dictionary<string, List<(ulong Subnet, ITopic Topic)>> _subscriptions = [];

    // A copy of _subscriptions read without _subscriptionLock: publishing may run under the pubsub router's monitor, which subscribing takes under that lock.
    private volatile Dictionary<string, List<(ulong Subnet, ITopic Topic)>> _publishTopics = [];

    private readonly LruKeyCache<Hash256> _reconstructedBlockRoots = new(SeenCacheSize, "beacon column reconstruction completed blocks");

    // The header signature of each root with a sidecar that passed every check, so columns reconstructed under that signed header may be published.
    private readonly LruCache<Hash256, BlsSignature> _acceptedHeaders = new(SeenCacheSize, "beacon column gossip accepted headers");
    private readonly HashSet<Hash256> _reconstructionsInFlight = [];
    private readonly Lock _reconstructionLock = new();

    internal DataColumnReconstructor Reconstruct { private get; init; } = DataColumnReconstruction.TryReconstruct;

    internal delegate bool DataColumnReconstructor(IReadOnlyList<DataColumnSidecar> heldColumns, out DataColumnSidecar[] fullMatrix);

    private Func<string, ITopic>? _getTopic;
    private IReadOnlyList<ulong> _subnets = [];

    /// <summary>
    /// Raised for a sidecar that passed every check this router can do without beacon state (see
    /// remarks) - not full spec acceptance.
    /// </summary>
    public event Action<DataColumnSidecar>? DataColumnSidecarReceived;

    public long GetDropCount(ColumnGossipDropReason reason) => Interlocked.Read(ref _dropCounts[(int)reason]);

    /// <summary>The Fulu KZG cell-proof batches this router has run.</summary>
    internal long KzgBatchCount => Interlocked.Read(ref _kzgBatches);

    /// <summary>The header signature pairings this router has run.</summary>
    internal long HeaderSignatureVerificationCount => Interlocked.Read(ref _headerSignatureVerifications);

    /// <summary>Subscribes <paramref name="subnets"/> for <paramref name="forkDigest"/>; every later digest subscribes the same subnets.</summary>
    public void Start(Func<string, ITopic> getTopic, byte[] forkDigest, IReadOnlyList<ulong> subnets)
    {
        lock (_subscriptionLock)
        {
            _getTopic = getTopic;
            _subnets = subnets;
            SubscribeDigest(forkDigest);
        }
    }

    /// <summary>Subscribes the subnets passed to <see cref="Start"/> for <paramref name="forkDigest"/>; does nothing when they are already subscribed.</summary>
    /// <remarks>Several digests are subscribed at once around a fork digest change (see <see cref="GossipTopics.DigestsAround"/>).</remarks>
    /// <exception cref="InvalidOperationException"><see cref="Start"/> has not run.</exception>
    public void SubscribeDigest(byte[] forkDigest)
    {
        lock (_subscriptionLock)
        {
            if (_getTopic is null)
            {
                throw new InvalidOperationException($"{nameof(ColumnGossipRouter)} is not started");
            }

            string key = Convert.ToHexStringLower(forkDigest);
            if (_subscriptions.ContainsKey(key))
            {
                return;
            }

            // The pubsub validator consumes every message (GossipMessageValidator); a topic handler would process an Accepted one twice.
            List<(ulong Subnet, ITopic Topic)> subscriptions = [];
            foreach (ulong subnetId in _subnets)
            {
                ITopic topic = _getTopic(GossipTopics.Topic(forkDigest, GossipTopics.DataColumnSidecarTopicName(subnetId)));
                topic.Subscribe();
                subscriptions.Add((subnetId, topic));
            }

            _subscriptions[key] = subscriptions;
            _publishTopics = new(_subscriptions);
            if (_logger.IsInfo) _logger.Info($"Subscribed {_subnets.Count} data column sidecar subnets for fork digest 0x{key}");
        }
    }

    /// <summary>Unsubscribes the subnets of <paramref name="forkDigest"/>; does nothing when none are subscribed.</summary>
    public void UnsubscribeDigest(byte[] forkDigest)
    {
        lock (_subscriptionLock)
        {
            string key = Convert.ToHexStringLower(forkDigest);
            if (!_subscriptions.Remove(key, out List<(ulong Subnet, ITopic Topic)>? subscriptions))
            {
                return;
            }

            _publishTopics = new(_subscriptions);

            foreach ((ulong _, ITopic topic) in subscriptions)
            {
                topic.Unsubscribe();
            }

            if (_logger.IsInfo) _logger.Info($"Unsubscribed data column sidecar subnets for fork digest 0x{key}");
        }
    }

    /// <summary>Validates a raw message from the <c>data_column_sidecar_{subnet_id}</c> topic of a Fulu or Gloas digest and consumes it when it passes.</summary>
    /// <returns>
    /// <see cref="MessageValidity.Accepted"/> for a Gloas sidecar that verified against its block's bid, or a Fulu sidecar that
    /// passed every check under an imported block's header or the expected proposer's; otherwise <see cref="MessageValidity.Rejected"/> or <see cref="MessageValidity.Ignored"/>,
    /// including for a consumed Fulu sidecar whose header this node cannot verify.
    /// </returns>
    internal MessageValidity Handle(ulong subnetId, bool gloasTopic, byte[] message) => Handle(subnetId, gloasTopic, message, GossipVerdict.None);

    /// <inheritdoc cref="Handle(ulong, bool, byte[])"/>
    /// <param name="verdict">The message's pending verdict, handed off with a Fulu sidecar queued until its expected proposer can be verified.</param>
    internal MessageValidity Handle(ulong subnetId, bool gloasTopic, byte[] message, GossipVerdict verdict)
    {
        bool retrying = _retrying;
        _retrying = false;
        try
        {
            return HandleReceived(subnetId, gloasTopic, message, verdict);
        }
        finally
        {
            _retrying = retrying;
        }
    }

    private MessageValidity HandleReceived(ulong subnetId, bool gloasTopic, byte[] message, GossipVerdict verdict)
    {
        if (!IsSubscribed(subnetId))
        {
            return Drop(ColumnGossipDropReason.UnsubscribedSubnet, MessageValidity.Ignored);
        }

        // The type bound caps only the uncompressed size; the compressed one is bounded by MAX_PAYLOAD_SIZE on every topic.
        if (message.Length > Eth2MessageId.MaxCompressedGossipSize)
        {
            return Drop(ColumnGossipDropReason.Oversized, MessageValidity.Rejected);
        }

        SnappyDecodeResult snappy = Eth2MessageId.TryDecompress(message, gloasTopic ? _maxGloasSidecarSize : Eth2MessageId.MaxGossipSize, out byte[]? payload);
        if (snappy != SnappyDecodeResult.Decoded)
        {
            return Drop(snappy == SnappyDecodeResult.Oversized ? ColumnGossipDropReason.Oversized : ColumnGossipDropReason.InvalidSnappy, MessageValidity.Rejected);
        }

        return gloasTopic ? HandleGloas(subnetId, payload!) : HandleFulu(subnetId, payload!, verdict);
    }

    private MessageValidity HandleFulu(ulong subnetId, byte[] payload, GossipVerdict verdict)
    {
        RetryParked();

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

        return ValidateFulu(subnetId, sidecar, verdict);
    }

    private MessageValidity ValidateFulu(ulong subnetId, DataColumnSidecar sidecar, GossipVerdict verdict)
    {
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

        // [REJECT] the sidecar is from a higher slot than its parent: the store holds only accepted blocks, so a held parent was seen and passed validation.
        if (_storedBlockSlots?.TryRead(header.ParentRoot!, _parentSlotReads, out ulong parentSlot) == true && slot <= parentSlot)
        {
            return Drop(ColumnGossipDropReason.NotAboveParentSlot, MessageValidity.Rejected);
        }

        // [REJECT] the block's parent passes validation: a failed parent is never imported, so this runs before any signature or KZG work.
        if (failedBlocks?.Contains(header.ParentRoot!) == true)
        {
            return Drop(ColumnGossipDropReason.FailedBlockValidation, MessageValidity.Rejected);
        }

        // [REJECT] the proposer signature is valid: an imported block's signature was verified at import.
        Hash256 blockRoot = SszRoots.HashTreeRoot(header);
        return ReadImportedHeader(blockRoot, sidecar.SignedBlockHeader.Signature) switch
        {
            ImportedHeader.Found => HandleImportedFulu(sidecar, blockRoot, slot, proposerIndex, verdict),
            // Only the signature verified at import is trusted without a BLS check, so another one is dropped, not rejected.
            ImportedHeader.SignatureMismatch => Drop(ColumnGossipDropReason.HeaderSignatureMismatch, MessageValidity.Ignored),
            _ => HandleUnimportedFulu(sidecar, header, blockRoot, slot, signatureVerifiedAtImport: false, verdict),
        };
    }

    /// <summary>The spec checks after the proposer signature, for a sidecar whose header is an imported block's.</summary>
    /// <remarks>
    /// Import already required the parent to be seen and valid and the slot to be above the parent's, so every check
    /// before the finalized-ancestor one has passed and a failure from it on is a REJECT. A sidecar that passes is
    /// <see cref="MessageValidity.Accepted"/>, so the pubsub library forwards it.
    /// </remarks>
    private MessageValidity HandleImportedFulu(DataColumnSidecar sidecar, Hash256 blockRoot, ulong slot, ulong proposerIndex, GossipVerdict verdict)
    {
        // [REJECT] the current finalized checkpoint is an ancestor of the block.
        switch (DescendsFromFinalized(CurrentSnapshot(), blockRoot))
        {
            case false:
                return Drop(ColumnGossipDropReason.NotFinalizedDescendant, MessageValidity.Rejected);
            case null:
                return HandleUnimportedFulu(sidecar, sidecar.SignedBlockHeader!.Message!, blockRoot, slot, signatureVerifiedAtImport: true, verdict);
        }

        // [REJECT] verify_data_column_sidecar_inclusion_proof.
        if (!DataColumnSidecarVerifier.VerifyInclusionProof(sidecar))
        {
            return Drop(ColumnGossipDropReason.FailedInclusionProof, MessageValidity.Rejected);
        }

        // A copy that verified before import leaves the tuple below unmarked, so without this every later copy costs a KZG batch.
        if (IsColumnVerified(blockRoot, sidecar.Index))
        {
            return ForwardIfHeld(sidecar, blockRoot, slot, proposerIndex);
        }

        // [REJECT] verify_data_column_sidecar_kzg_proofs: a native KZG cell-proof batch, run inline on the pubsub read loop.
        if (VerifyKzgProofs(sidecar, blockRoot) is { } kzgFailure)
        {
            return Drop(kzgFailure, kzgFailure == ColumnGossipDropReason.FailedKzgProofs ? MessageValidity.Rejected : MessageValidity.Ignored);
        }

        return ConsumeVerified(sidecar, blockRoot, slot, proposerIndex);
    }

    /// <summary>Consumes a sidecar that passed every check and returns <see cref="MessageValidity.Accepted"/>, so the pubsub library forwards it.</summary>
    private MessageValidity ConsumeVerified(DataColumnSidecar sidecar, Hash256 blockRoot, ulong slot, ulong proposerIndex)
    {
        // [IGNORE] the first sidecar for (slot, proposer_index, index) with a valid header signature, inclusion proof and KZG proofs.
        if (!_seenSidecars.Set((slot, proposerIndex, sidecar.Index)))
        {
            return Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);
        }

        // Before the consume, which may reconstruct the block's other columns and publish them under this header.
        _acceptedHeaders.Set(blockRoot, sidecar.SignedBlockHeader!.Signature);
        return TryConsume(sidecar, blockRoot, slot) ? Accept(MessageValidity.Accepted) : Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);
    }

    /// <summary>
    /// Returns <see cref="MessageValidity.Accepted"/> for the first copy of a (slot, proposer_index, index) whose cells and
    /// proofs equal the pooled copy of its column, which verified under the same header; any other copy is a duplicate.
    /// </summary>
    /// <remarks>
    /// A column consumed without being forwarded, such as one pooled by sync or one whose checks could not all run yet,
    /// leaves its tuple unmarked. The inclusion proof binds the commitments to the header, so an equal copy needs no KZG batch.
    /// </remarks>
    private MessageValidity ForwardIfHeld(DataColumnSidecar sidecar, Hash256 blockRoot, ulong slot, ulong proposerIndex) =>
        pool is not null && pool.TryGet(blockRoot, sidecar.Index, out DataColumnSidecar? held) && held is not null
            && MemoryMarshal.AsBytes<SszBlobCell>(held.Column).SequenceEqual(MemoryMarshal.AsBytes<SszBlobCell>(sidecar.Column))
            && MemoryMarshal.AsBytes<SszKzgCommitment>(held.KzgProofs).SequenceEqual(MemoryMarshal.AsBytes<SszKzgCommitment>(sidecar.KzgProofs))
            && _seenSidecars.Set((slot, proposerIndex, sidecar.Index))
            ? Accept(MessageValidity.Accepted)
            : Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);

    /// <summary>The spec checks for a sidecar whose header is not a block fork choice holds with known finalized ancestry.</summary>
    /// <remarks>
    /// A failure is <see cref="MessageValidity.Rejected"/> only when every check ordered before it ran and passed: the parent
    /// is in fork choice, its finalized ancestry is known, and the signature was checked; a source this router has none of, such as
    /// fork choice or the key cache, applies no rule. A sidecar that passes every check
    /// under a header whose proposer is verified is <see cref="MessageValidity.Accepted"/>; one that passes only the checks
    /// that could run is <see cref="MessageValidity.Ignored"/> and is marked seen only by header root, never by the
    /// (slot, proposer_index, index) it claims.
    /// </remarks>
    /// <param name="signatureVerifiedAtImport">Whether the header is a stored block's with the signature and proposer import verified.</param>
    private MessageValidity HandleUnimportedFulu(DataColumnSidecar sidecar, BeaconBlockHeader header, Hash256 blockRoot, ulong slot, bool signatureVerifiedAtImport, GossipVerdict verdict)
    {
        // [IGNORE] the parent has been seen, and [REJECT] it passes validation: fork choice holds only valid blocks.
        SnapshotIndex? snapshot = CurrentSnapshot();
        ForkChoiceSnapshotNode? parent = null;
        if (snapshot is not null && header.ParentRoot is { } parentRoot)
        {
            snapshot.Nodes.TryGetValue(parentRoot, out parent);
        }

        bool rejectable = parent is not null || forkChoice is null;

        // [REJECT] the expected proposer: checked first as it needs no BLS work; import verified an imported block's.
        ExpectedProposer? proposer = signatureVerifiedAtImport ? ExpectedProposer.Expected
            : proposerLookahead is null ? null
            : CheckExpectedProposer(snapshot, parent, header);
        if (proposer == ExpectedProposer.Unexpected)
        {
            return Drop(ColumnGossipDropReason.UnexpectedProposer, MessageValidity.Rejected);
        }

        // [REJECT] the proposer index is a valid validator index and the proposer signature is valid.
        bool signatureValid = signatureVerifiedAtImport;
        if (!signatureVerifiedAtImport)
        {
            switch (CheckHeaderSignature(header, blockRoot, sidecar.SignedBlockHeader!.Signature))
            {
                case HeaderSignature.Valid:
                    signatureValid = true;
                    break;
                case HeaderSignature.UnknownProposer:
                    return Drop(ColumnGossipDropReason.UnknownProposer, MessageValidity.Ignored);
                case HeaderSignature.Invalid:
                    return Drop(ColumnGossipDropReason.InvalidHeaderSignature, rejectable ? MessageValidity.Rejected : MessageValidity.Ignored);
            }
        }

        if (parent is not null)
        {
            // [REJECT] the sidecar is from a higher slot than its parent.
            if (header.Slot <= parent.Slot)
            {
                return Drop(ColumnGossipDropReason.NotAboveParentSlot, rejectable ? MessageValidity.Rejected : MessageValidity.Ignored);
            }

            // [REJECT] the current finalized checkpoint is an ancestor of the sidecar's block.
            switch (DescendsFromFinalized(snapshot, parent.Root))
            {
                case false:
                    return Drop(ColumnGossipDropReason.NotFinalizedDescendant, rejectable ? MessageValidity.Rejected : MessageValidity.Ignored);
                case null:
                    rejectable = false;
                    break;
            }
        }

        // [REJECT] verify_data_column_sidecar_inclusion_proof: a few SHA256 hashes, run before any KZG work.
        if (!DataColumnSidecarVerifier.VerifyInclusionProof(sidecar))
        {
            return Drop(ColumnGossipDropReason.FailedInclusionProof, rejectable ? MessageValidity.Rejected : MessageValidity.Ignored);
        }

        if (IsColumnVerified(blockRoot, sidecar.Index))
        {
            return proposer == ExpectedProposer.Expected && rejectable
                ? ForwardIfHeld(sidecar, blockRoot, slot, header.ProposerIndex)
                : Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);
        }

        if (!TryAdmitSignedHeader(header.Slot, header.ProposerIndex, blockRoot))
        {
            return Drop(ColumnGossipDropReason.ProposerHeaderLimit, MessageValidity.Ignored);
        }

        // An expected proposer that cannot be verified yet is the spec's IGNORE, "MAY be queued": it runs no KZG work, the only expensive check.
        if (proposer == ExpectedProposer.Unverifiable)
        {
            if (signatureValid)
            {
                ParkUnverifiable(sidecar, blockRoot, verdict);
            }

            return Drop(ColumnGossipDropReason.ProposerNotVerifiable, MessageValidity.Ignored);
        }

        // [REJECT] verify_data_column_sidecar_kzg_proofs: a sidecar reaches the pool only after it passes, so a forgery never displaces the honest copy.
        if (VerifyKzgProofs(sidecar, blockRoot) is { } kzgFailure)
        {
            return Drop(kzgFailure, rejectable && kzgFailure == ColumnGossipDropReason.FailedKzgProofs ? MessageValidity.Rejected : MessageValidity.Ignored);
        }

        if (proposer == ExpectedProposer.Expected && rejectable)
        {
            return ConsumeVerified(sidecar, blockRoot, slot, header.ProposerIndex);
        }

        return TryConsume(sidecar, blockRoot, slot)
            ? Accept(MessageValidity.Ignored)
            : Drop(ColumnGossipDropReason.Duplicate, MessageValidity.Ignored);
    }

    /// <summary>
    /// The phase0 p2p-interface.md <c>beacon_block</c> rules that need beacon state, run for a Fulu block's header against the published
    /// fork-choice snapshot, proposer lookahead and key cache, as for a sidecar's header.
    /// </summary>
    /// <returns>
    /// <see cref="BlockHeaderCheck.Verified"/> when the parent is held and valid, the slot is above it, the finalized checkpoint is an ancestor,
    /// the proposer is the expected one and its signature verifies; a failing REJECT rule; or <see cref="BlockHeaderCheck.Unverifiable"/>
    /// when a source cannot answer yet, which the spec turns into an IGNORE.
    /// </returns>
    /// <remarks>The expected proposer is checked before the signature, so a flood naming another proposer costs no BLS work.</remarks>
    internal BlockHeaderCheck CheckBlockHeader(BeaconBlockHeader header, Hash256 blockRoot, BlsSignature signature)
    {
        // [IGNORE] the parent has been seen, and [REJECT] it passes validation: fork choice holds only valid blocks.
        SnapshotIndex? snapshot = CurrentSnapshot();
        if (snapshot is null || header.ParentRoot is null || !snapshot.Nodes.TryGetValue(header.ParentRoot, out ForkChoiceSnapshotNode? parent))
        {
            return BlockHeaderCheck.Unverifiable;
        }

        // bellatrix p2p-interface.md beacon_block: a parent whose execution payload is invalidated does not pass validation; ignored, as fork choice keeps the node.
        if (parent.ExecutionStatus == ExecutionStatus.Invalid)
        {
            return BlockHeaderCheck.Unverifiable;
        }

        // [REJECT] the block is from a higher slot than its parent.
        if (header.Slot <= parent.Slot)
        {
            return BlockHeaderCheck.NotAboveParentSlot;
        }

        // [REJECT] the current finalized checkpoint is an ancestor of the block.
        switch (DescendsFromFinalized(snapshot, parent.Root))
        {
            case false:
                return BlockHeaderCheck.NotFinalizedDescendant;
            case null:
                return BlockHeaderCheck.Unverifiable;
        }

        // [REJECT] the block is proposed by the expected proposer_index for its slot on its branch.
        switch (proposerLookahead is null ? ExpectedProposer.Unverifiable : CheckExpectedProposer(snapshot, parent, header))
        {
            case ExpectedProposer.Unexpected:
                return BlockHeaderCheck.UnexpectedProposer;
            case ExpectedProposer.Unverifiable:
                return BlockHeaderCheck.Unverifiable;
        }

        // [REJECT] the proposer signature is valid.
        return CheckHeaderSignature(header, blockRoot, signature) switch
        {
            HeaderSignature.Valid => BlockHeaderCheck.Verified,
            HeaderSignature.Invalid => BlockHeaderCheck.InvalidSignature,
            _ => BlockHeaderCheck.Unverifiable,
        };
    }

    /// <summary>fulu/p2p-interface.md: the header's proposer is the expected one for its slot in the shuffling of its branch.</summary>
    /// <remarks>
    /// The published lookahead answers only for a branch whose latest block before the lookahead's first slot is its
    /// dependent root. Any other branch, or a slot outside the lookahead, cannot be verified immediately, which the spec
    /// turns into an IGNORE.
    /// </remarks>
    private ExpectedProposer CheckExpectedProposer(SnapshotIndex? snapshot, ForkChoiceSnapshotNode? parent, BeaconBlockHeader header)
    {
        if (snapshot is null || parent is null || proposerLookahead!.Current is not { } lookahead || !lookahead.TryGetProposer(header.Slot, out ulong expected))
        {
            return ExpectedProposer.Unverifiable;
        }

        if (ProposerLookaheadSnapshot.FindDependentRoot(snapshot.Snapshot.Nodes, parent.Root, lookahead.StartSlot) != lookahead.DependentRoot)
        {
            return ExpectedProposer.Unverifiable;
        }

        return expected == header.ProposerIndex ? ExpectedProposer.Expected : ExpectedProposer.Unexpected;
    }

    /// <summary>fulu/p2p-interface.md: the header's proposer index is in the validator key cache and its signature verifies under <c>DOMAIN_BEACON_PROPOSER</c>.</summary>
    /// <remarks>
    /// The key cache may briefly lag the head's registry, so an index outside it is unknown rather than invalid. The domain
    /// is the fork version scheduled for the header's epoch, which is what <c>get_domain</c> on the head state returns for it.
    /// </remarks>
    private HeaderSignature CheckHeaderSignature(BeaconBlockHeader header, Hash256 blockRoot, BlsSignature signature)
    {
        if (pubkeys is null)
        {
            return HeaderSignature.Unchecked;
        }

        if (_headerSignatures.TryGet(blockRoot, out HeaderSignatureCheck cached) && cached.Signature == signature)
        {
            return cached.Valid ? HeaderSignature.Valid : HeaderSignature.Invalid;
        }

        bool valid;
        try
        {
            if (header.ProposerIndex >= (ulong)pubkeys.Count)
            {
                return HeaderSignature.UnknownProposer;
            }

            Hash256 domain = Domains.ComputeDomain(DomainType.BeaconProposer, spec.VersionForEpoch(spec.GetEpoch(header.Slot)), spec.GenesisValidatorsRoot);
            Interlocked.Increment(ref _headerSignatureVerifications);
            valid = pubkeys.TryGetValidPublicKey((int)header.ProposerIndex, out G1Affine key)
                && BlsSigner.Verify(key, signature.Bytes, Domains.ComputeSigningRoot(blockRoot, domain).Bytes);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The import worker replaced the key buffer between the count read and the key read.
            return HeaderSignature.UnknownProposer;
        }

        // A header has one valid signature, so a verified one is never displaced by a failed one.
        if (valid || !cached.Valid)
        {
            _headerSignatures.Set(blockRoot, new HeaderSignatureCheck(signature, valid));
        }

        return valid ? HeaderSignature.Valid : HeaderSignature.Invalid;
    }

    /// <summary>Admits <paramref name="blockRoot"/> for KZG work unless (<paramref name="slot"/>, <paramref name="proposerIndex"/>) already has <see cref="SignedHeadersPerProposal"/> other roots.</summary>
    private bool TryAdmitSignedHeader(ulong slot, ulong proposerIndex, Hash256 blockRoot)
    {
        lock (_signedHeaderRootsLock)
        {
            Hash256[] roots = _signedHeaderRoots.Get((slot, proposerIndex)) ?? [];
            if (Array.IndexOf(roots, blockRoot) >= 0)
            {
                return true;
            }

            if (roots.Length >= SignedHeadersPerProposal)
            {
                return false;
            }

            _signedHeaderRoots.Set((slot, proposerIndex), [.. roots, blockRoot]);
            return true;
        }
    }

    /// <summary>Queues a signed sidecar whose expected proposer cannot be verified yet, keeping at most <see cref="KzgBatchesPerColumn"/> copies per (block root, index).</summary>
    /// <remarks>
    /// More copies could never reach KZG, since a verified copy ends the column's KZG work and failures are bounded. A queued copy keeps
    /// its message's verdict, so the router forwards that message if a retry accepts it.
    /// </remarks>
    private void ParkUnverifiable(DataColumnSidecar sidecar, Hash256 blockRoot, GossipVerdict verdict)
    {
        (Hash256, ulong) key = (blockRoot, sidecar.Index);
        lock (_parkedLock)
        {
            ParkedColumn[] copies = _parkedColumns.Get(key) ?? [];
            if (copies.Length == 0 && ParkedKeysOf(sidecar.SignedBlockHeader!.Message!.ProposerIndex) >= ParkedColumnsPerProposer)
            {
                return;
            }

            if (copies.Length < KzgBatchesPerColumn)
            {
                _parkedColumns.Set(key, [.. copies, new ParkedColumn(sidecar, verdict)]);
                verdict.HandOff();
            }
        }
    }

    private int ParkedKeysOf(ulong proposerIndex)
    {
        int keys = 0;
        foreach (KeyValuePair<(Hash256 BlockRoot, ulong Index), ParkedColumn[]> entry in _parkedColumns.ToArray())
        {
            if (entry.Value[0].Sidecar.SignedBlockHeader!.Message!.ProposerIndex == proposerIndex)
            {
                keys++;
            }
        }

        return keys;
    }

    /// <summary>Runs every queued sidecar through the checks again once a new fork-choice snapshot or proposer lookahead is published.</summary>
    /// <remarks>
    /// The retry gives the queued message's verdict, so the router forwards a sidecar that now passes, and charges its sender for one that
    /// now fails a REJECT rule. A sidecar whose verdict the router no longer awaits is pooled but not forwarded, so its tuple stays unmarked
    /// for an equal copy that reaches this router. One that still cannot be verified is queued again.
    /// </remarks>
    private void RetryParked()
    {
        ForkChoiceSnapshot? snapshot = forkChoice?.Current;
        ProposerLookaheadSnapshot? lookahead = proposerLookahead?.Current;
        if (ReferenceEquals(snapshot, Volatile.Read(ref _retriedSnapshot)) && ReferenceEquals(lookahead, Volatile.Read(ref _retriedLookahead)))
        {
            return;
        }

        KeyValuePair<(Hash256 BlockRoot, ulong Index), ParkedColumn[]>[] parked;
        lock (_parkedLock)
        {
            if (ReferenceEquals(snapshot, _retriedSnapshot) && ReferenceEquals(lookahead, _retriedLookahead))
            {
                return;
            }

            Volatile.Write(ref _retriedSnapshot, snapshot);
            Volatile.Write(ref _retriedLookahead, lookahead);
            if (_parkedColumns.Count == 0)
            {
                return;
            }

            parked = _parkedColumns.ToArray();
            _parkedColumns.Clear();
        }

        bool retrying = _retrying;
        _retrying = true;
        try
        {
            foreach (KeyValuePair<(Hash256 BlockRoot, ulong Index), ParkedColumn[]> entry in parked)
            {
                foreach ((DataColumnSidecar sidecar, GossipVerdict verdict) in entry.Value)
                {
                    // The handler that queued the copy may still be returning, so its hand-off is never undone; a new one means queued again.
                    int handOffs = verdict.HandOffCount;
                    MessageValidity validity = ValidateFulu(CustodyGroups.ComputeSubnetForDataColumnSidecar(sidecar.Index), sidecar, verdict);
                    if (verdict.HandOffCount != handOffs)
                    {
                        continue;
                    }

                    if (!verdict.Complete(validity) && validity == MessageValidity.Accepted && sidecar.SignedBlockHeader?.Message is { } header)
                    {
                        _seenSidecars.Delete((header.Slot, header.ProposerIndex, sidecar.Index));
                    }
                }
            }
        }
        finally
        {
            _retrying = retrying;
        }
    }

    /// <summary>Adds a sidecar whose KZG proofs passed to the pool and raises <see cref="DataColumnSidecarReceived"/>, unless a copy for its block root and index already did.</summary>
    private bool TryConsume(DataColumnSidecar sidecar, Hash256 blockRoot, ulong slot)
    {
        if (!_verifiedColumns.Set((blockRoot, sidecar.Index)))
        {
            return false;
        }

        pool?.Add(blockRoot, slot, sidecar);
        DataColumnSidecarReceived?.Invoke(sidecar);

        TrackHeldColumnAndMaybeReconstruct(blockRoot, slot);
        return true;
    }

    // The pool holds only sidecars whose proofs verified, so a pooled copy counts as verified too.
    private bool IsColumnVerified(Hash256 blockRoot, ulong index) =>
        _verifiedColumns.Get((blockRoot, index)) || (pool is not null && pool.TryGet(blockRoot, index, out _));

    /// <summary>Runs <c>verify_data_column_sidecar_kzg_proofs</c> unless <see cref="KzgBatchesPerColumn"/> batches already ran for the sidecar's (block root, index).</summary>
    /// <returns><c>null</c> when the proofs verify; otherwise why the sidecar is dropped.</returns>
    /// <remarks>A copy that verifies is held, so later copies of its column never reach this; only failures use up the bound.</remarks>
    private ColumnGossipDropReason? VerifyKzgProofs(DataColumnSidecar sidecar, Hash256 blockRoot)
    {
        (Hash256, ulong) key = (blockRoot, sidecar.Index);

        // Counted before the batch runs, so concurrent copies never run more batches than the bound.
        lock (_kzgBatchesLock)
        {
            _kzgBatchesByColumn.TryGet(key, out int batches);
            if (batches >= KzgBatchesPerColumn)
            {
                return ColumnGossipDropReason.KzgBatchLimit;
            }

            _kzgBatchesByColumn.Set(key, batches + 1);
        }

        Interlocked.Increment(ref _kzgBatches);
        return DataColumnSidecarVerifier.VerifyKzgProofs(sidecar) ? null : ColumnGossipDropReason.FailedKzgProofs;
    }

    /// <summary>Whether <paramref name="blockRoot"/> is a block this node imported, and if so whether <paramref name="signature"/> is the one it was imported with.</summary>
    /// <remarks>
    /// A header that hashes to a stored root is that block's header, and the store holds only blocks whose proposer
    /// signature was verified at import. Such roots lie within the slot window checked before, so this decodes at most
    /// the stored blocks of that window, each once.
    /// </remarks>
    private ImportedHeader ReadImportedHeader(Hash256 blockRoot, BlsSignature signature)
    {
        if (!_importedHeaderSignatures.TryGet(blockRoot, out BlsSignature? imported))
        {
            if (store is null || !store.HasBlock(blockRoot))
            {
                return ImportedHeader.Unknown;
            }

            try
            {
                imported = store.TryGetForkedBlock(blockRoot, out ForkedSignedBeaconBlock? forked) && forked is ForkedSignedBeaconBlock.OfFulu fulu
                    ? fulu.Block.Signature
                    : null;
            }
            catch (Exception e) when (e is BeaconStateException or InvalidDataException)
            {
                if (_logger.IsWarn) _logger.Warn($"Unreadable stored block {blockRoot} named by a data column sidecar: {e.Message}");
                imported = null;
            }

            _importedHeaderSignatures.Set(blockRoot, imported);
        }

        return imported switch
        {
            null => ImportedHeader.Unknown,
            { } stored when stored == signature => ImportedHeader.Found,
            _ => ImportedHeader.SignatureMismatch,
        };
    }

    /// <summary>The latest fork-choice snapshot with its nodes and their finalized ancestry indexed by root, or <c>null</c> when none has been taken.</summary>
    private SnapshotIndex? CurrentSnapshot()
    {
        if (forkChoice?.Current is not { } snapshot)
        {
            return null;
        }

        SnapshotIndex? index = _snapshotIndex;
        if (index is null || !ReferenceEquals(index.Snapshot, snapshot))
        {
            _snapshotIndex = index = SnapshotIndex.Build(snapshot);
        }

        return index;
    }

    /// <summary>
    /// fork-choice.md <c>get_checkpoint_block(store, root, finalized.epoch) == finalized.root</c>, or <c>null</c> when
    /// <paramref name="snapshot"/> cannot answer it. With no snapshot source the rule is not applied.
    /// </summary>
    private bool? DescendsFromFinalized(SnapshotIndex? snapshot, Hash256 blockRoot)
    {
        if (forkChoice is null)
        {
            return true;
        }

        return snapshot is not null && snapshot.DescendsFromFinalized.TryGetValue(blockRoot, out bool descends) ? descends : null;
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

        // A sidecar no bid can match is dropped before a block decode or parking. The spec orders its REJECT after the future-slot
        // and block-seen IGNOREs, so only a sidecar whose block is held is rejected: that block's bid is within the blob limit, so a longer column fails too.
        bool blockCached = _gloasBlocks.TryGet(blockRoot, out GloasBlockColumns block);
        if (!blockCached && ExceedsCandidateBounds(sidecar) is { } boundsReason)
        {
            bool convicted = ValidateNotFromFuture(sidecar.Slot) is null && store?.HasBlock(blockRoot) == true;
            return Drop(boundsReason, convicted ? MessageValidity.Rejected : MessageValidity.Ignored);
        }

        // [IGNORE] not from a future slot. One early for the next slot is parked: its message id stays dropped for the seen TTL.
        if (ValidateNotFromFuture(sidecar.Slot) is { } futureReason)
        {
            return sidecar.Slot == slotClock.CurrentSlot + 1
                ? Park(sidecar, futureReason)
                : Drop(futureReason, MessageValidity.Ignored);
        }

        // No Gloas block precedes the fork, so such a sidecar can match none; this also makes every stored pre-Gloas block a slot mismatch below.
        if (!blockCached && spec.GetEpoch(sidecar.Slot) < spec.GloasForkEpoch)
        {
            return Drop(ColumnGossipDropReason.UnknownBlock, MessageValidity.Ignored);
        }

        // [REJECT] the block passes validation: a refused block is never stored, so it would otherwise read as unseen and be parked.
        if (failedBlocks?.Contains(blockRoot) == true)
        {
            return Drop(ColumnGossipDropReason.FailedBlockValidation, MessageValidity.Rejected);
        }

        // [IGNORE] the block has been seen, and [REJECT] it passes validation: the store holds only blocks fork choice accepted.
        switch (blockCached ? GloasBlockLookup.Found : ReadGloasBlock(blockRoot, sidecar.Slot, out block))
        {
            case GloasBlockLookup.Unknown:
                return Park(sidecar, ColumnGossipDropReason.UnknownBlock);
            case GloasBlockLookup.Unreadable:
                return Drop(ColumnGossipDropReason.UnknownBlock, MessageValidity.Ignored);
            case GloasBlockLookup.BudgetSpent:
                return Drop(ColumnGossipDropReason.StoreDecodeBudgetSpent, MessageValidity.Ignored);
            case GloasBlockLookup.PreGloas:
                // [REJECT] the sidecar's slot matches the slot of the block: a pre-Gloas block's slot is before the sidecar's.
                return Drop(ColumnGossipDropReason.SlotMismatch, MessageValidity.Rejected);
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
        return Accept(MessageValidity.Accepted);
    }

    /// <summary>
    /// Keeps a sidecar whose block is not yet held as a pending candidate (gloas/p2p-interface.md "MAY be queued until
    /// block is retrieved") when it is for the current or next slot, the only slots the future-slot IGNORE lets through.
    /// </summary>
    /// <remarks>
    /// The pool does not keep the peer that delivered a candidate, and <c>StrictNoSign</c> requires the message's
    /// <c>from</c> to be absent, so a candidate has no source to be bounded by: the pool bounds candidates per
    /// (root, column) and in total instead, and a forgery never displaces an earlier candidate.
    /// </remarks>
    private MessageValidity Park(DataColumnSidecarGloas sidecar, ColumnGossipDropReason reason)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        if (sidecar.Slot == currentSlot || sidecar.Slot == currentSlot + 1)
        {
            pool?.AddPendingGloas(sidecar, currentSlot);
        }

        return Drop(reason, MessageValidity.Ignored);
    }

    /// <summary>The stateless part of <c>verify_data_column_sidecar</c> plus the blob limit every accepted bid is within, or <c>null</c> when it passes.</summary>
    private ColumnGossipDropReason? ExceedsCandidateBounds(DataColumnSidecarGloas sidecar)
    {
        if (sidecar.Index >= (ulong)Eip7594DasConstants.NumberOfColumns
            || sidecar.Column is not { Length: > 0 } column
            || sidecar.KzgProofs?.Length != column.Length)
        {
            return ColumnGossipDropReason.FailedStructure;
        }

        // A bid never commits more than max_blobs_per_block, so a longer column can never verify against one.
        return (ulong)column.Length > MaxBlobsPerBlock(sidecar.Slot) ? ColumnGossipDropReason.FailedBlobCount : null;
    }

    private GloasBlockLookup ReadGloasBlock(Hash256 blockRoot, ulong sidecarSlot, out GloasBlockColumns block)
    {
        block = default;
        if (_nonGloasBlocks.TryGet(blockRoot, out GloasBlockLookup cached))
        {
            return cached;
        }

        if (store is null || !store.HasBlock(blockRoot))
        {
            return GloasBlockLookup.Unknown;
        }

        if (store.TryGetBlockSummary(blockRoot, out StoredBlockSummary summary))
        {
            return CacheSummary(blockRoot, summary, out block);
        }

        if (!IsCanonicalAtRecentSlot(blockRoot, sidecarSlot) && !_storeDecodes.TryTake(slotClock.CurrentSlot))
        {
            return GloasBlockLookup.BudgetSpent;
        }

        ForkedSignedBeaconBlock? forked;
        try
        {
            if (!store.TryGetForkedBlock(blockRoot, out forked))
            {
                return GloasBlockLookup.Unknown;
            }
        }
        catch (Exception e) when (e is BeaconStateException or InvalidDataException)
        {
            if (_logger.IsWarn) _logger.Warn($"Unreadable stored block {blockRoot} named by a data column sidecar: {e.Message}");
            _nonGloasBlocks.Set(blockRoot, GloasBlockLookup.Unreadable);
            return GloasBlockLookup.Unreadable;
        }

        // Persist the slot and bid used by gloas/p2p-interface.md sidecar validation after a legacy decode.
        return CacheSummary(blockRoot, store.PutBlockSummary(blockRoot, forked), out block);
    }

    private GloasBlockLookup CacheSummary(Hash256 blockRoot, StoredBlockSummary summary, out GloasBlockColumns block)
    {
        block = default;
        if (!summary.IsGloas)
        {
            _nonGloasBlocks.Set(blockRoot, GloasBlockLookup.PreGloas);
            return GloasBlockLookup.PreGloas;
        }

        block = new GloasBlockColumns(summary.Slot, summary.Commitments);
        _gloasBlocks.Set(blockRoot, block);
        return GloasBlockLookup.Found;
    }

    // The canonical index maps a slot to the root of the block at that slot, so such a root is a Gloas block matching the sidecar's slot.
    private bool IsCanonicalAtRecentSlot(Hash256 blockRoot, ulong sidecarSlot)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        return (sidecarSlot == currentSlot || sidecarSlot + 1 == currentSlot)
            && store!.TryGetCanonicalRoot(sidecarSlot, out Hash256? canonical)
            && canonical == blockRoot;
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

    /// <summary>Recovers and exposes missing columns once enough are held (fulu/das-core.md recover_matrix).</summary>
    /// <remarks>A root stays claimed through publication; cell recovery and subscriber callbacks run outside the lock.</remarks>
    private void TrackHeldColumnAndMaybeReconstruct(Hash256 blockRoot, ulong slot)
    {
        DataColumnSidecar[] held;
        lock (_reconstructionLock)
        {
            if (pool is null || _reconstructedBlockRoots.Get(blockRoot) || _reconstructionsInFlight.Contains(blockRoot)
                || !pool.TryGetHeldColumns(blockRoot, slot, Eip7594DasConstants.RequiredColumnsForReconstruction, out held!))
            {
                return;
            }

            _reconstructionsInFlight.Add(blockRoot);
        }

        List<ReconstructedSidecarToPublish> exposed = [];
        try
        {
            if (!Reconstruct(held, out DataColumnSidecar[] fullMatrix))
            {
                return;
            }

            IReadOnlyList<ReconstructedSidecarToPublish> entries = ReconstructionBroadcast.SelectNewlyReconstructed(held, fullMatrix);
            lock (_reconstructionLock)
            {
                foreach (ReconstructedSidecarToPublish entry in entries)
                {
                    if (ExposeReconstructed(entry, blockRoot))
                    {
                        exposed.Add(entry);
                    }
                }

                _reconstructedBlockRoots.Set(blockRoot);
            }
        }
        finally
        {
            lock (_reconstructionLock)
            {
                _reconstructionsInFlight.Remove(blockRoot);
            }
        }

        foreach (ReconstructedSidecarToPublish entry in exposed)
        {
            DataColumnSidecarReceived?.Invoke(entry.Sidecar);
        }

        PublishReconstructed(blockRoot, exposed);
    }

    /// <summary>Adds a locally reconstructed sidecar to the serving pool, unless a gossip copy of its (block root, index) already did.</summary>
    private bool ExposeReconstructed(ReconstructedSidecarToPublish entry, Hash256 blockRoot)
    {
        if (!_verifiedColumns.Set((blockRoot, entry.Sidecar.Index)))
        {
            return false;
        }

        pool?.Add(blockRoot, entry.Slot, entry.Sidecar);
        return true;
    }

    /// <summary>Sends each reconstructed sidecar of a subscribed subnet to that subnet's topic of the sidecar's own fork digest.</summary>
    /// <remarks>
    /// fulu/das-core.md "Reconstruction and cross-seeding": a column of a subscribed subnet MUST go to the topic mesh neighbors.
    /// A column of any other subnet is only held: its SHOULD-expose through gossip emission is not done. A sidecar is published only under
    /// the signed header of a sidecar of its block that passed every check, since a held column consumed unverified or pooled by sync may
    /// carry a forged signature, which reconstruction copies. A failed send releases its (slot, proposer_index, index) tuple, so a later
    /// gossip copy is free to be forwarded.
    /// </remarks>
    private void PublishReconstructed(Hash256 blockRoot, List<ReconstructedSidecarToPublish> entries)
    {
        if (entries.Count == 0 || !_acceptedHeaders.TryGet(blockRoot, out BlsSignature accepted))
        {
            return;
        }

        // Every entry is a column of one block, so of one slot and one digest.
        _publishTopics.TryGetValue(Convert.ToHexStringLower(ForkDigest.Compute(spec, spec.GetEpoch(entries[0].Slot))), out List<(ulong Subnet, ITopic Topic)>? subscribed);
        foreach (ReconstructedSidecarToPublish entry in entries)
        {
            // A reconstructed sidecar carries the signed header of a held column, which sync may have pooled under another signature.
            ITopic? topic = subscribed?.Find(subscription => subscription.Subnet == entry.Subnet).Topic;
            if (topic is null || entry.Sidecar.SignedBlockHeader?.Signature != accepted)
            {
                continue;
            }

            // [IGNORE] the first sidecar for (slot, proposer_index, index): claimed as a received copy would be, so an equivocating block's column is not sent.
            (ulong, ulong, ulong) tuple = (entry.Slot, entry.ProposerIndex, entry.Sidecar.Index);
            if (!_seenSidecars.Set(tuple))
            {
                continue;
            }

            try
            {
                topic.Publish(Snappy.CompressToArray(DataColumnSidecar.Encode(entry.Sidecar)));
            }
            catch (InvalidOperationException e)
            {
                _seenSidecars.Delete(tuple);
                if (_logger.IsDebug) _logger.Debug($"Could not publish reconstructed data column {entry.Sidecar.Index} at slot {entry.Slot}: {e.Message}");
            }
        }
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

    [ThreadStatic]
    private static bool _retrying;

    private static readonly StringLabel[] DropReasonLabels = Array.ConvertAll(Enum.GetValues<ColumnGossipDropReason>(), static reason => new StringLabel(reason.ToString()));

    /// <summary>Counts a consumed sidecar as accepted, whether it is forwarded or, unverified, only consumed.</summary>
    private static MessageValidity Accept(MessageValidity validity)
    {
        if (!_retrying) Interlocked.Increment(ref Metrics.GossipAcceptedCount);
        return validity;
    }

    private MessageValidity Drop(ColumnGossipDropReason reason, MessageValidity validity)
    {
        Interlocked.Increment(ref _dropCounts[(int)reason]);
        if (!_retrying)
        {
            Interlocked.Increment(ref Metrics.GossipDroppedCount);
            Metrics.BeaconChainColumnGossipDroppedByReason.Increment(DropReasonLabels[(int)reason]);
        }

        if (_logger.IsTrace) _logger.Trace($"Dropped data column sidecar gossip message: {reason}");
        return validity;
    }

    private readonly record struct GloasBlockColumns(ulong Slot, SszKzgCommitment[] Commitments);

    private readonly record struct ParkedColumn(DataColumnSidecar Sidecar, GossipVerdict Verdict);

    private enum GloasBlockLookup
    {
        Found,
        Unknown,
        PreGloas,
        Unreadable,
        BudgetSpent,
    }

    private enum ImportedHeader
    {
        Unknown,
        Found,
        SignatureMismatch,
    }

    /// <summary>The outcome of <see cref="CheckBlockHeader"/>.</summary>
    internal enum BlockHeaderCheck
    {
        Verified,
        Unverifiable,
        NotAboveParentSlot,
        NotFinalizedDescendant,
        UnexpectedProposer,
        InvalidSignature,
    }

    private enum ExpectedProposer
    {
        Expected,
        Unexpected,
        Unverifiable,
    }

    private enum HeaderSignature
    {
        Valid,
        Invalid,
        UnknownProposer,
        Unchecked,
    }

    private readonly record struct HeaderSignatureCheck(BlsSignature Signature, bool Valid);

    /// <param name="DescendsFromFinalized">Whether each node's checkpoint block at the finalized epoch is the finalized root; a node whose ancestry leaves the snapshot has no entry.</param>
    private sealed record SnapshotIndex(ForkChoiceSnapshot Snapshot, Dictionary<Hash256, ForkChoiceSnapshotNode> Nodes, Dictionary<Hash256, bool> DescendsFromFinalized)
    {
        /// <remarks>One pass in proto-array order, parents before children, so a check costs a lookup, not a walk to the finalized slot.</remarks>
        public static SnapshotIndex Build(ForkChoiceSnapshot snapshot)
        {
            CheckpointRef finalized = snapshot.FinalizedCheckpoint;
            ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(finalized.Epoch);
            Dictionary<Hash256, ForkChoiceSnapshotNode> nodes = new(snapshot.Nodes.Count);
            Dictionary<Hash256, bool> descends = new(snapshot.Nodes.Count);
            foreach (ForkChoiceSnapshotNode node in snapshot.Nodes)
            {
                nodes[node.Root] = node;
                if (node.Slot <= finalizedSlot)
                {
                    descends[node.Root] = node.Root == finalized.Root;
                }
                else if (node.ParentRoot is { } parentRoot && descends.TryGetValue(parentRoot, out bool parentDescends))
                {
                    descends[node.Root] = parentDescends;
                }
            }

            return new SnapshotIndex(snapshot, nodes, descends);
        }
    }
}
