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

/// <summary>The Fulu <c>data_column_sidecars_by_range</c> v1 protocol, dialable for Fulu or Gloas-shaped sidecars.</summary>
/// <remarks>
/// The listen side serves custodied columns of either fork from the local <see cref="DataColumnSidecarPool"/>,
/// skipping slots or columns it does not hold. It serves only the block <see cref="BeaconChainStore"/>
/// records as canonical at each slot, since fulu/p2p-interface.md requires the response to follow the
/// responder's view of the current fork choice; a competing block's columns are never served.
/// The dial side validates per-chunk fork-digest context
/// bytes (in the shared base) and that every returned sidecar's slot and column were actually asked for.
/// A Gloas dial's window must lie wholly in Gloas epochs.
/// </remarks>
public sealed class DataColumnSidecarsByRangeProtocol(BeaconChainSpec spec, DataColumnSidecarPool pool, BeaconChainStore store) : DataColumnSidecarsProtocolBase(spec),
    ISessionProtocol<DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>
{
    /// <summary>Fixed part (2 x Uint64 + a 4-byte list offset) plus the variable columns list, bounded by NUMBER_OF_COLUMNS: an upper bound for framing, not an exact length (the columns list may be shorter).</summary>
    private const int MaxRequestLength = 2 * sizeof(ulong) + 4 + Eip7594DasConstants.NumberOfColumns * sizeof(ulong);

    public string Id => "/eth2/beacon_chain/req/data_column_sidecars_by_range/1/ssz_snappy";

    /// <exception cref="ArgumentOutOfRangeException">The columns are empty or too many; or, for a Gloas dial, the window is empty, runs past the last slot or starts before the Gloas fork.</exception>
    public async Task<ForkedDataColumnSidecars> DialAsync(IChannel downChannel, ISessionContext context, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest> dial) =>
        dial.Gloas
            ? new ForkedDataColumnSidecars([], await DialGloasAsync(downChannel, dial.Request))
            : new ForkedDataColumnSidecars(await DialFuluAsync(downChannel, dial.Request), []);

    private async Task<IReadOnlyList<DataColumnSidecar>> DialFuluAsync(IChannel downChannel, DataColumnSidecarsByRangeRequest request)
    {
        int requestedColumns = ValidateRequestedColumns(request.Columns, nameof(request));

        Stream stream = new ChannelStreamAdapter(downChannel);
        using (CancellationTokenSource cts = StartTimeout(RespTimeout))
        {
            await WriteRequestAndEofAsync(downChannel, stream, DataColumnSidecarsByRangeRequest.Encode(request), cts.Token);
        }

        IReadOnlyList<DataColumnSidecar> sidecars = await ReadSidecarChunksAsync(stream, MaxSidecars(request.Count, requestedColumns), Id);

        HashSet<ulong> requestedColumnSet = [.. request.Columns!];
        foreach (DataColumnSidecar sidecar in sidecars)
        {
            ThrowIfNotRequested(request.StartSlot, request.Count, requestedColumnSet, sidecar.SignedBlockHeader!.Message!.Slot, sidecar.Index);
        }

        return sidecars;
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
                    else if (pool.TryGetGloas(root, column, out DataColumnSidecarGloas? gloasSidecar))
                    {
                        await WriteGloasSidecarChunkAsync(stream, gloasSidecar, cts);
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
