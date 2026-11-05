// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using L1PostingContext = Nethermind.Eez.Prove.V2.L1PostingContext;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Attester;

internal sealed class RollupXParentAuthorizer
{
    internal const int PageBlocks = 128;
    private readonly RollupXAuthorizationEvidence _evidence;
    private readonly SettlementDeployment _deployment;
    private readonly RollupXSchedule _schedule;
    private readonly Hash256 _genesisHash;
    private readonly int _historyBlocks;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;

    internal RollupXParentAuthorizer(SettlementRpcReader l1, SettlementRpcReader l2, Address registry, Address manager,
        EezSettlementContext context, Address signer, RollupXSchedule schedule, Hash256 genesisHash,
        int historyBlocks = 4096, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        if (schedule.BlockIntervalSeconds == 0 || schedule.SyncIntervalBlocks < 2 ||
            schedule.BlockIntervalSeconds > ulong.MaxValue / schedule.SyncIntervalBlocks || schedule.L1Reference.ChainId == 0 ||
            schedule.L1Reference.Hash == Keccak.Zero || genesisHash == Keccak.Zero ||
            schedule.GenesisTimestamp < schedule.L1Reference.Timestamp ||
            (schedule.GenesisTimestamp - schedule.L1Reference.Timestamp) % schedule.L1SlotSeconds != 0 ||
            historyBlocks is < 1 or > 4096)
            throw new ArgumentException("authorization requires a valid schedule and bounded history");
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout < TimeSpan.FromMilliseconds(1) || _timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _evidence = new RollupXAuthorizationEvidence(l1, l2, registry, context.RollupId);
        _deployment = new SettlementDeployment(l1, registry, manager, context, signer);
        _schedule = schedule;
        _genesisHash = genesisHash;
        _historyBlocks = historyBlocks;
        _time = time ?? TimeProvider.System;
    }

    internal async Task<RollupXAuthorization> Authorize(Begin predecessor, L1PostingContext posting, CancellationToken cancellation, ParentProvenance? provenance = null)
    {
        cancellation.ThrowIfCancellationRequested();
        Begin selected = predecessor.Clone();
        L1PostingContext target = posting.Clone();
        ParentProvenance? supplied = provenance?.Clone();
        if (selected.RollupId != _deployment.Context.RollupId || selected.AnchorHash.Length != 32 || selected.AnchorStateRoot.Length != 32 ||
            target.ChainId != _schedule.L1Reference.ChainId || target.ParentHash.Length != 32 ||
            target.ParentNumber < _schedule.L1Reference.Number || target.ParentNumber == ulong.MaxValue ||
            !target.ParentHash.Span.ContainsAnyExcept((byte)0)) throw RollupXAuthorizationEvidence.Rejected();
        using CancellationTokenSource deadline = new(_timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        try { return await AuthorizeSelected(selected, target, supplied, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw SettlementRpcReader.Unavailable(); }
    }

    private async Task<RollupXAuthorization> AuthorizeSelected(Begin selected, L1PostingContext target, ParentProvenance? provenance, CancellationToken cancellation)
    {
        await _evidence.CheckChains(_schedule.L1Reference.ChainId, _deployment.Context.ChainId, cancellation).ConfigureAwait(false);
        CanonicalL1Header reference = await _evidence.Reference(_schedule.L1Reference, cancellation).ConfigureAwait(false);
        CanonicalL1Header l1Parent = await _evidence.HeaderAt(target.ParentNumber, cancellation).ConfigureAwait(false);
        if (!l1Parent.Hash.Bytes.SequenceEqual(target.ParentHash.Span)) throw RollupXAuthorizationEvidence.Stale();
        if (target.TargetTimestamp <= l1Parent.Timestamp || target.TargetTimestamp < reference.Timestamp ||
            (target.TargetTimestamp - reference.Timestamp) % _schedule.L1SlotSeconds != 0) throw RollupXAuthorizationEvidence.Rejected();
        List<CanonicalL1Header> snapshots = new(68) { reference };
        RecordSnapshot(snapshots, l1Parent);
        BlockHeader parent = await _evidence.L2Header(new Hash256(selected.AnchorHash.Span), cancellation).ConfigureAwait(false);
        if (parent.Number != selected.AnchorNumber || !parent.StateRoot!.Bytes.SequenceEqual(selected.AnchorStateRoot.Span) ||
            parent.Timestamp != _schedule.Timestamp(parent.Number)) throw RollupXAuthorizationEvidence.Rejected();

        if (provenance is not null && !Rlp.Encode(parent).Bytes.AsSpan().SequenceEqual(provenance.ParentHeaderRlp.Span))
            throw RollupXAuthorizationEvidence.Rejected();
        List<ByteString> endpointHeaders = [];
        CanonicalL1Header? accepted = null;
        CanonicalL1Header? liveSlot = null;
        Hash256 seed = parent.MixHash!;
        if (parent.Number == 0)
        {
            if (parent.Hash != _genesisHash || seed != reference.MixHash) throw RollupXAuthorizationEvidence.Rejected();
        }
        else
        {
            ValidateEndpoint(parent);
            liveSlot = await SlotAt(parent.Timestamp, reference, l1Parent, snapshots, cancellation).ConfigureAwait(false);
            Hash256[] liveEndpoints = liveSlot is null ? [] : await _evidence.Endpoints(liveSlot, cancellation).ConfigureAwait(false);
            foreach (Hash256 hash in liveEndpoints)
            {
                BlockHeader endpoint = await _evidence.L2Header(hash, cancellation).ConfigureAwait(false);
                ValidateEndpoint(endpoint);
                if (provenance is not null) endpointHeaders.Add(ByteString.CopyFrom(Rlp.Encode(endpoint).Bytes));
                RollupXAnchorKind kind = _schedule.AnchorKind(endpoint.Number, liveSlot!.Timestamp) ?? throw RollupXAuthorizationEvidence.Rejected();
                if (endpoint.Number == parent.Number && kind == RollupXAnchorKind.Live) seed = liveSlot.MixHash;
            }
            if (provenance is not null)
            {
                if (provenance.AcceptedL1Number < reference.Number || provenance.AcceptedL1Number > l1Parent.Number || provenance.AcceptedL1Number == 0)
                    throw RollupXAuthorizationEvidence.Rejected();
                if (liveSlot is not null && provenance.AcceptedL1Number == liveSlot.Number && Contains(liveEndpoints, parent.Hash!)) accepted = liveSlot;
                else
                {
                    accepted = await _evidence.HeaderAt(provenance.AcceptedL1Number, cancellation).ConfigureAwait(false);
                    if (!Contains(await _evidence.Endpoints(accepted, cancellation).ConfigureAwait(false), parent.Hash!))
                        throw RollupXAuthorizationEvidence.Rejected();
                }
            }
            else if (liveSlot is not null && Contains(liveEndpoints, parent.Hash!)) accepted = liveSlot;
            else accepted = await Acceptance(parent.Hash!, l1Parent, cancellation).ConfigureAwait(false);
            _ = _schedule.AnchorKind(parent.Number, accepted.Timestamp) ?? throw RollupXAuthorizationEvidence.Rejected();
        }

        if (provenance is not null)
        {
            if (provenance.AcceptedL1Number != (accepted?.Number ?? 0) || provenance.LiveSlotEndpointHeadersRlp.Count != endpointHeaders.Count)
                throw RollupXAuthorizationEvidence.Rejected();
            for (int i = 0; i < endpointHeaders.Count; i++)
                if (endpointHeaders[i] != provenance.LiveSlotEndpointHeadersRlp[i]) throw RollupXAuthorizationEvidence.Rejected();
        }

        if (accepted is not null) RecordSnapshot(snapshots, accepted);
        if (liveSlot is not null) RecordSnapshot(snapshots, liveSlot);
        Hash256 registryRoot = await _deployment.ReadRoot(l1Parent.Snapshot, cancellation).ConfigureAwait(false);
        if (registryRoot == Keccak.Zero) throw RollupXAuthorizationEvidence.Rejected();
        await _deployment.ValidateVerifier(l1Parent.Snapshot, cancellation).ConfigureAwait(false);
        snapshots.Remove(l1Parent);
        snapshots.Add(l1Parent);
        RollupXAuthorization result = new(this, _deployment.Context, _deployment.Signer, ByteString.CopyFrom(Rlp.Encode(parent).Bytes),
            new L1PostingTarget(target.TargetTimestamp, l1Parent.Hash), l1Parent.Number, registryRoot, seed, [.. snapshots]);
        await RecheckCore(result, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    internal async Task Recheck(RollupXAuthorization authorization, CancellationToken cancellation)
    {
        if (!ReferenceEquals(authorization.Authorizer, this)) throw RollupXAuthorizationEvidence.Rejected();
        using CancellationTokenSource deadline = new(_timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        try { await RecheckCore(authorization, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw SettlementRpcReader.Unavailable(); }
    }

    private async Task RecheckCore(RollupXAuthorization authorization, CancellationToken cancellation)
    {
        foreach (CanonicalL1Header snapshot in authorization.CanonicalEvidence)
        {
            CanonicalL1Header current = await _evidence.HeaderAt(snapshot.Number, cancellation).ConfigureAwait(false);
            if (current != snapshot) throw RollupXAuthorizationEvidence.Stale();
        }
    }

    private async Task<CanonicalL1Header?> SlotAt(ulong timestamp, CanonicalL1Header reference, CanonicalL1Header through,
        List<CanonicalL1Header> snapshots, CancellationToken cancellation)
    {
        if (timestamp < reference.Timestamp || timestamp > through.Timestamp) return null;
        ulong low = reference.Number, high = through.Number;
        for (int reads = 0; low <= high && reads < 64; reads++)
        {
            cancellation.ThrowIfCancellationRequested();
            ulong middle = low + (high - low) / 2;
            CanonicalL1Header header = await _evidence.HeaderAt(middle, cancellation).ConfigureAwait(false);
            RecordSnapshot(snapshots, header);
            if (header.Timestamp == timestamp) return header;
            if (header.Timestamp < timestamp)
            {
                if (middle == ulong.MaxValue) return null;
                low = middle + 1;
            }
            else
            {
                if (middle == 0) return null;
                high = middle - 1;
            }
        }
        if (low <= high) throw SettlementRpcReader.Unavailable();
        return null;
    }

    private async Task<CanonicalL1Header> Acceptance(Hash256 parent, CanonicalL1Header through, CancellationToken cancellation)
    {
        ulong floor = Math.Max(_schedule.L1Reference.Number, through.Number >= (ulong)_historyBlocks - 1 ? through.Number - (ulong)_historyBlocks + 1 : 0);
        ulong end = through.Number;
        int candidates = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            ulong start = Math.Max(floor, end >= PageBlocks - 1 ? end - PageBlocks + 1 : 0);
            CanonicalLog[] roots = await _evidence.Roots(start, end, cancellation).ConfigureAwait(false);
            HashSet<ulong> checkedBlocks = [];
            for (int i = roots.Length - 1; i >= 0; i--)
            {
                CanonicalLog root = roots[i];
                if (!root.Data.AsSpan(0, 32).SequenceEqual(parent.Bytes) || !checkedBlocks.Add(root.Number)) continue;
                if (++candidates > RollupXAuthorizationEvidence.MaxBatches) throw SettlementRpcReader.Unavailable();
                CanonicalL1Header block = await _evidence.HeaderAt(root.Number, cancellation).ConfigureAwait(false);
                if (block.Hash != root.BlockHash) throw RollupXAuthorizationEvidence.Stale();
                if (Contains(await _evidence.Endpoints(block, cancellation).ConfigureAwait(false), parent)) return block;
            }
            if (start == floor) break;
            end = start - 1;
        }
        throw SettlementRpcReader.Unavailable();
    }

    private static void RecordSnapshot(List<CanonicalL1Header> snapshots, CanonicalL1Header header)
    {
        foreach (CanonicalL1Header previous in snapshots)
        {
            if (previous.Number != header.Number) continue;
            if (previous != header) throw RollupXAuthorizationEvidence.Stale();
            return;
        }
        snapshots.Add(header);
    }

    private void ValidateEndpoint(BlockHeader header)
    {
        if (header.Number == 0 || header.ExtraData.Length > DaPayloadCodec.MaxExtraData ||
            RollupXHeaderValidator.ProfileMismatch(header, _schedule) is not null) throw RollupXAuthorizationEvidence.Rejected();
    }

    private static bool Contains(Hash256[] hashes, Hash256 hash)
    {
        foreach (Hash256 candidate in hashes) if (candidate == hash) return true;
        return false;
    }
}

internal sealed record RollupXAuthorization(RollupXParentAuthorizer Authorizer, EezSettlementContext Context, Address Signer,
    ByteString ParentHeader, L1PostingTarget Target, ulong L1ParentNumber, Hash256 RegistryRoot, Hash256 DescendantSeed,
    ImmutableArray<CanonicalL1Header> CanonicalEvidence);
