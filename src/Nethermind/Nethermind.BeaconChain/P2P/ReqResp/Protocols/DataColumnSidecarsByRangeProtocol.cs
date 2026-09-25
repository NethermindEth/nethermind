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
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The Fulu <c>data_column_sidecars_by_range</c> v1 protocol.</summary>
/// <remarks>
/// The listen side serves custodied columns from the local <see cref="DataColumnSidecarPool"/>,
/// skipping slots or columns it does not hold. It serves only the block <see cref="BeaconChainStore"/>
/// records as canonical at each slot, since fulu/p2p-interface.md requires the response to follow the
/// responder's view of the current fork choice; a competing block's columns are never served.
/// The dial side validates per-chunk fork-digest context
/// bytes (in the shared base) and that every returned sidecar's slot and column were actually asked for.
/// </remarks>
public sealed class DataColumnSidecarsByRangeProtocol(BeaconChainSpec spec, DataColumnSidecarPool pool, BeaconChainStore store) : DataColumnSidecarsProtocolBase(spec),
    ISessionProtocol<DataColumnSidecarsByRangeRequest, IReadOnlyList<DataColumnSidecar>>
{
    /// <summary>Fixed part (2 x Uint64 + a 4-byte list offset) plus the variable columns list, bounded by NUMBER_OF_COLUMNS: an upper bound for framing, not an exact length (the columns list may be shorter).</summary>
    private const int MaxRequestLength = 2 * sizeof(ulong) + 4 + Eip7594DasConstants.NumberOfColumns * sizeof(ulong);

    public string Id => "/eth2/beacon_chain/req/data_column_sidecars_by_range/1/ssz_snappy";

    public async Task<IReadOnlyList<DataColumnSidecar>> DialAsync(IChannel downChannel, ISessionContext context, DataColumnSidecarsByRangeRequest request)
    {
        int requestedColumns = request.Columns?.Length ?? 0;
        if (requestedColumns == 0 || requestedColumns > Eip7594DasConstants.NumberOfColumns)
        {
            // Never trust an oversized/empty ask on the wire: reject it before dialing rather than
            // let the listen side's fixed-size ReadRequestAsync bound be the only thing catching this.
            throw new ArgumentOutOfRangeException(nameof(request), requestedColumns, $"Columns must be 1..{Eip7594DasConstants.NumberOfColumns}");
        }

        Stream stream = new ChannelStreamAdapter(downChannel);
        using (CancellationTokenSource cts = StartTimeout(RespTimeout))
        {
            await WriteRequestAndEofAsync(downChannel, stream, DataColumnSidecarsByRangeRequest.Encode(request), cts.Token);
        }

        ulong slotCount = Math.Min(request.Count, BlocksProtocolBase.MaxRequestBlocks);
        int maxSidecars = (int)Math.Min(MaxRequestDataColumnSidecars, slotCount * (ulong)requestedColumns);
        IReadOnlyList<DataColumnSidecar> sidecars = await ReadSidecarChunksAsync(stream, maxSidecars, Id);

        HashSet<ulong> requestedColumnSet = [.. request.Columns!];
        foreach (DataColumnSidecar sidecar in sidecars)
        {
            ulong slot = sidecar.SignedBlockHeader!.Message!.Slot;
            if (slot < request.StartSlot || slot - request.StartSlot >= request.Count || !requestedColumnSet.Contains(sidecar.Index))
            {
                RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
                throw new Eth2ReqRespException($"Data column sidecar at slot {slot} index {sidecar.Index} outside the requested range or columns");
            }
        }

        return sidecars;
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using IDisposable? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            return;
        }

        using BoundedTimeout timeout = StartBoundedTimeout(RespTimeout, MaxSidecarsResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            byte[] requestSsz = await ReqRespFraming.ReadRequestAsync(stream, MaxRequestLength, cts.Token);
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
            for (ulong slot = request.StartSlot; slot < request.StartSlot + count; slot++)
            {
                if (!store.TryGetCanonicalRoot(slot, out Hash256? root))
                {
                    continue;
                }

                foreach (ulong column in orderedColumns)
                {
                    if (pool.TryGet(root, column, out DataColumnSidecar? sidecar))
                    {
                        await WriteSidecarChunkAsync(stream, sidecar!, cts);
                    }
                }
            }
        }
        catch (Eth2ReqRespException e)
        {
            RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
        }
    }
}
