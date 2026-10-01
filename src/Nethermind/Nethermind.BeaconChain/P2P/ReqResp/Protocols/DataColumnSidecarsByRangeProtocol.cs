// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The Fulu <c>data_column_sidecars_by_range</c> v1 protocol, dialable for Fulu or Gloas-shaped sidecars.</summary>
/// <remarks>
/// The listen side serves custodied columns of either fork from the local <see cref="DataColumnSidecarPool"/>,
/// skipping slots or columns it does not hold; unreadable held columns at or above the servable floor end the reply with
/// a <c>ServerError</c> (fulu/p2p-interface.md, DataColumnSidecarsByRange). It serves only the block <see cref="BeaconChainStore"/>
/// records as canonical at each slot, since fulu/p2p-interface.md requires the response to follow the
/// responder's view of the current fork choice; a competing block's columns are never served.
/// The dial side validates per-chunk fork-digest context
/// bytes (in the shared base) and that every returned sidecar's slot and column were actually asked for.
/// A Gloas dial's window must lie wholly in Gloas epochs.
/// With a <see cref="SlotClock"/>, a request whose part inside <c>data_column_serve_range</c> starts below
/// <see cref="DataColumnSidecarPool.EarliestCompletelyServableSlot"/> is answered <c>ResourceUnavailable</c>,
/// as fulu/p2p-interface.md asks of a peer unable to reply within that range; without one, no range is checked.
/// </remarks>
public sealed class DataColumnSidecarsByRangeProtocol(BeaconChainSpec spec, DataColumnSidecarPool pool, BeaconChainStore store, SlotClock? clock = null) : DataColumnSidecarsProtocolBase(spec),
    ISessionProtocol<DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>
{
    /// <summary>Fixed part (2 x Uint64 + a 4-byte list offset) plus the variable columns list, bounded by NUMBER_OF_COLUMNS: an upper bound for framing, not an exact length (the columns list may be shorter).</summary>
    private const int MaxRequestLength = 2 * sizeof(ulong) + 4 + Eip7594DasConstants.NumberOfColumns * sizeof(ulong);

    /// <summary>Time allowed for one expected chunk, on top of the first-byte allowance.</summary>
    private static readonly TimeSpan PerChunkAllowance = TimeSpan.FromMilliseconds(250);

    /// <summary>Not a spec constant: bounds how long the scaled budget of one by-range reply can grow.</summary>
    private static readonly TimeSpan MaxResponseBudget = TimeSpan.FromMinutes(2);

    public string Id => "/eth2/beacon_chain/req/data_column_sidecars_by_range/1/ssz_snappy";

    /// <exception cref="ArgumentOutOfRangeException">The columns are empty or too many; or, for a Gloas dial, the window is empty, runs past the last slot or starts before the Gloas fork.</exception>
    public async Task<ForkedDataColumnSidecars> DialAsync(IChannel downChannel, ISessionContext context, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest> dial) =>
        dial.Gloas
            ? new ForkedDataColumnSidecars([], await DialGloasAsync(downChannel, dial.Request))
            : new ForkedDataColumnSidecars(await DialFuluAsync(downChannel, dial.Request, dial.OnSidecar), []);

    private async Task<IReadOnlyList<DataColumnSidecar>> DialFuluAsync(IChannel downChannel, DataColumnSidecarsByRangeRequest request, Action<DataColumnSidecar>? onSidecar)
    {
        int requestedColumns = ValidateRequestedColumns(request.Columns, nameof(request));

        Stream stream = new ChannelStreamAdapter(downChannel);
        using (CancellationTokenSource cts = StartTimeout(RespTimeout))
        {
            await WriteRequestAndEofAsync(downChannel, stream, DataColumnSidecarsByRangeRequest.Encode(request), cts.Token);
        }

        HashSet<ulong> requestedColumnSet = [.. request.Columns!];
        return await ReadSidecarChunksAsync(
            stream,
            MaxSidecars(request.Count, requestedColumns),
            Id,
            ResponseBudget(request.Count, requestedColumns),
            sidecar =>
            {
                ThrowIfNotRequested(request.StartSlot, request.Count, requestedColumnSet, sidecar.SignedBlockHeader!.Message!.Slot, sidecar.Index);
                onSidecar?.Invoke(sidecar);
            });
    }

    /// <summary>
    /// How long a by-range reply of up to <paramref name="slots"/> x <paramref name="columns"/> chunks may take end to end: the
    /// first-byte allowance plus a per-chunk allowance, so a slow peer that keeps delivering is not cut by a per-slot guess.
    /// </summary>
    /// <remarks>Each chunk must still arrive within the per-chunk read timeout, so a stalled peer is cut long before this.</remarks>
    internal static TimeSpan ResponseBudget(ulong slots, int columns)
    {
        ulong bounded = Math.Min(slots, BlocksProtocolBase.MaxRequestBlocks);
        ulong chunks = bounded * (ulong)Math.Max(columns, 0);
        ulong maxChunks = (ulong)(MaxResponseBudget.Ticks / PerChunkAllowance.Ticks);
        TimeSpan scaled = TimeSpan.FromTicks((long)Math.Min(chunks, maxChunks) * PerChunkAllowance.Ticks);
        TimeSpan perSlotFloor = TimeSpan.FromSeconds(bounded);
        TimeSpan budget = TtfbTimeout + RespTimeout + (scaled > perSlotFloor ? scaled : perSlotFloor);
        return budget < MaxResponseBudget ? budget : MaxResponseBudget;
    }

    private async Task<IReadOnlyList<DataColumnSidecarGloas>> DialGloasAsync(IChannel downChannel, DataColumnSidecarsByRangeRequest request)
    {
        int requestedColumns = ValidateRequestedColumns(request.Columns, nameof(request));
        if (request.Count == 0 || request.Count - 1 > ulong.MaxValue - request.StartSlot || Spec.GetEpoch(request.StartSlot) < Spec.GloasForkEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.StartSlot, $"The window of {request.Count} slots from slot {request.StartSlot} must be non-empty and lie wholly in Gloas epochs");
        }

        Stream stream = new ChannelStreamAdapter(downChannel);
        using (CancellationTokenSource cts = StartTimeout(RespTimeout))
        {
            await WriteRequestAndEofAsync(downChannel, stream, DataColumnSidecarsByRangeRequest.Encode(request), cts.Token);
        }

        IReadOnlyList<DataColumnSidecarGloas> sidecars = await ReadGloasSidecarChunksAsync(stream, MaxSidecars(request.Count, requestedColumns), Id);

        HashSet<ulong> requestedColumnSet = [.. request.Columns!];
        foreach (DataColumnSidecarGloas sidecar in sidecars)
        {
            ThrowIfNotRequested(request.StartSlot, request.Count, requestedColumnSet, sidecar.Slot, sidecar.Index);
        }

        return sidecars;
    }

    // Never trust an oversized/empty ask on the wire: reject it before dialing rather than
    // let the listen side's fixed-size ReadRequestAsync bound be the only thing catching this.
    private static int ValidateRequestedColumns(ulong[]? columns, string paramName)
    {
        int requestedColumns = columns?.Length ?? 0;
        if (requestedColumns == 0 || requestedColumns > Eip7594DasConstants.NumberOfColumns)
        {
            throw new ArgumentOutOfRangeException(paramName, requestedColumns, $"Columns must be 1..{Eip7594DasConstants.NumberOfColumns}");
        }

        return requestedColumns;
    }

    private static int MaxSidecars(ulong count, int requestedColumns) =>
        (int)Math.Min(MaxRequestDataColumnSidecars, Math.Min(count, BlocksProtocolBase.MaxRequestBlocks) * (ulong)requestedColumns);

    private void ThrowIfNotRequested(ulong startSlot, ulong count, HashSet<ulong> requestedColumns, ulong slot, ulong column)
    {
        if (slot < startSlot || slot - startSlot >= count || !requestedColumns.Contains(column))
        {
            RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            throw new Eth2ReqRespException($"Data column sidecar at slot {slot} index {column} outside the requested range or columns");
        }
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream wire = new ChannelStreamAdapter(downChannel);
        await using InboundRequest? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            return;
        }

        using BoundedTimeout timeout = StartBoundedTimeout(RespTimeout, MaxSidecarsResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            byte[] requestSsz = await inboundSlot.ReadRequestAsync(wire, MaxRequestLength, cts.Token);
            DataColumnSidecarsByRangeRequest request;
            try
            {
                DataColumnSidecarsByRangeRequest.Decode(requestSsz, out request);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Eth2ReqRespException($"Malformed data-column-sidecars-by-range request: {e.Message}");
            }

            if (request.Count == 0 || request.Columns is not { Length: > 0 } columns || columns.Length > Eip7594DasConstants.NumberOfColumns)
            {
                throw new Eth2ReqRespException($"Request must have a positive count and 1..{Eip7594DasConstants.NumberOfColumns} columns");
            }

            // fulu/p2p-interface.md: sidecars MUST be sent in (slot, column_index) order.
            ulong[] orderedColumns = [.. new SortedSet<ulong>(columns)];
            ulong count = Math.Min(request.Count, BlocksProtocolBase.MaxRequestBlocks);
            if (clock is not null && StartsBelowCompleteColumns(request.StartSlot, count, clock))
            {
                throw new Eth2ReqRespException("Requested range reaches below the earliest slot whose columns are all held", ReqRespFraming.ResponseCode.ResourceUnavailable);
            }

            List<(DataColumnSidecar? Fulu, DataColumnSidecarGloas? Gloas)> sidecars = [];
            for (ulong slot = request.StartSlot; slot < request.StartSlot + count; slot++)
            {
                if (!store.TryGetCanonicalRoot(slot, out Hash256? root))
                {
                    continue;
                }

                // fulu/p2p-interface.md: include all requested columns of each included block; resolve reads before writing.
                sidecars.Clear();
                foreach (ulong column in orderedColumns)
                {
                    bool read = TryRead(root, column, out DataColumnSidecar? fulu, out DataColumnSidecarGloas? gloas);
                    if (!read && slot >= pool.EarliestCompletelyServableSlot && StoreHoldsUnreadable(root, column))
                    {
                        // Recheck concurrent arrivals before ending the reply (fulu/p2p-interface.md, DataColumnSidecarsByRange).
                        read = TryRead(root, column, out fulu, out gloas);
                        if (!read)
                        {
                            throw new Eth2ReqRespException($"Data column sidecar at slot {slot} index {column} could not be read", ReqRespFraming.ResponseCode.ServerError);
                        }
                    }

                    if (read)
                    {
                        sidecars.Add((fulu, gloas));
                    }
                }

                foreach ((DataColumnSidecar? fulu, DataColumnSidecarGloas? gloas) in sidecars)
                {
                    if (fulu is not null)
                    {
                        await WriteSidecarChunkAsync(wire, fulu, cts);
                    }
                    else
                    {
                        await WriteGloasSidecarChunkAsync(wire, gloas!, cts);
                    }
                }
            }
        }
        catch (Eth2ReqRespException e)
        {
            if (e.ResponseCode is not (ReqRespFraming.ResponseCode.ResourceUnavailable or ReqRespFraming.ResponseCode.ServerError))
            {
                RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            }

            await ReqRespFraming.WriteErrorChunkAsync(wire, e.ResponseCode, e.Message, cts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
        }
    }

    private bool TryRead(Hash256 root, ulong column, out DataColumnSidecar? fulu, out DataColumnSidecarGloas? gloas)
    {
        gloas = null;
        return pool.TryGet(root, column, out fulu) || pool.TryGetGloas(root, column, out gloas);
    }

    // Failed reads must not omit held columns (fulu/p2p-interface.md, DataColumnSidecarsByRange).
    private bool StoreHoldsUnreadable(Hash256 root, ulong column)
    {
        try
        {
            return store.HasDataColumnRecord(root, column);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
        {
            return true;
        }
    }

    // Below data_column_serve_range the columns MAY be served as held, so only the part inside it is checked.
    private bool StartsBelowCompleteColumns(ulong startSlot, ulong count, SlotClock slotClock)
    {
        ulong lastSlot = count - 1 > ulong.MaxValue - startSlot ? ulong.MaxValue : startSlot + count - 1;
        ulong serveFrom = Math.Max(startSlot, DataAvailabilityBoundary.ComputeStartSlot(slotClock.CurrentEpoch, Spec));
        ulong serveTo = Math.Min(lastSlot, slotClock.CurrentSlot);
        return serveFrom <= serveTo && serveFrom < pool.EarliestCompletelyServableSlot;
    }
}

/// <summary>A by-range column request that failed after delivering some sidecars; carries them so the caller keeps what arrived.</summary>
/// <remarks>The message is the cause's, so failure classification by text is unchanged.</remarks>
internal sealed class PartialSidecarsException(Exception cause, IReadOnlyList<DataColumnSidecar> received) : Exception(cause.GetBaseException().Message, cause)
{
    /// <summary>The sidecars read before the failure: structurally valid and inside the requested window, not yet verified against their blocks.</summary>
    public IReadOnlyList<DataColumnSidecar> Received { get; } = received;
}
