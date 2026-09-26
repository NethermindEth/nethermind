// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The Fulu <c>data_column_sidecars_by_root</c> v1 protocol, dialable for Fulu or Gloas-shaped sidecars.</summary>
/// <remarks>
/// The listen side serves whichever requested (root, column) pairs the local
/// <see cref="DataColumnSidecarPool"/> has verified, in either shape; an unverified Gloas candidate is never served.
/// The dial side takes each returned sidecar's block root (recomputed from a Fulu header, or a Gloas
/// sidecar's own <c>beacon_block_root</c>) and rejects sidecars whose (root, column) was not requested or repeats.
/// </remarks>
public sealed class DataColumnSidecarsByRootProtocol(BeaconChainSpec spec, DataColumnSidecarPool pool) : DataColumnSidecarsProtocolBase(spec),
    ISessionProtocol<DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>, ForkedDataColumnSidecars>
{
    /// <summary>Per identifier: its 4-byte offset in the outer list, a 32-byte root, a 4-byte columns offset and the columns list, bounded by NUMBER_OF_COLUMNS.</summary>
    private const int MaxIdentifierBytes = 4 + 32 /* Hash256.Size */ + 4 + Eip7594DasConstants.NumberOfColumns * sizeof(ulong);

    /// <summary>An upper bound for framing (identifiers list capped at MAX_REQUEST_BLOCKS_DENEB), not an exact length.</summary>
    private const int MaxRequestLength = (int)BlocksProtocolBase.MaxRequestBlocks * MaxIdentifierBytes;

    public string Id => "/eth2/beacon_chain/req/data_column_sidecars_by_root/1/ssz_snappy";

    /// <exception cref="ArgumentOutOfRangeException">Too many roots, an identifier with no or too many columns, too many columns in total, or a repeated (root, column).</exception>
    public async Task<ForkedDataColumnSidecars> DialAsync(IChannel downChannel, ISessionContext context, DataColumnSidecarsDial<DataColumnsByRootIdentifier[]> dial) =>
        dial.Gloas
            ? new ForkedDataColumnSidecars([], await DialGloasAsync(downChannel, dial.Request))
            : new ForkedDataColumnSidecars(await DialFuluAsync(downChannel, dial.Request), []);

    private async Task<IReadOnlyList<DataColumnSidecar>> DialFuluAsync(IChannel downChannel, DataColumnsByRootIdentifier[] request)
    {
        (Stream stream, int totalRequestedColumns, Dictionary<Hash256, HashSet<ulong>> requested) = await WriteRequestAsync(downChannel, request);
        IReadOnlyList<DataColumnSidecar> sidecars = await ReadSidecarChunksAsync(stream, totalRequestedColumns, Id);

        foreach (DataColumnSidecar sidecar in sidecars)
        {
            TakeRequested(requested, SszRoots.HashTreeRoot(sidecar.SignedBlockHeader!.Message!), sidecar.Index);
        }

        return sidecars;
    }

    private async Task<IReadOnlyList<DataColumnSidecarGloas>> DialGloasAsync(IChannel downChannel, DataColumnsByRootIdentifier[] request)
    {
        (Stream stream, int totalRequestedColumns, Dictionary<Hash256, HashSet<ulong>> requested) = await WriteRequestAsync(downChannel, request);
        IReadOnlyList<DataColumnSidecarGloas> sidecars = await ReadGloasSidecarChunksAsync(stream, totalRequestedColumns, Id);

        foreach (DataColumnSidecarGloas sidecar in sidecars)
        {
            TakeRequested(requested, sidecar.BeaconBlockRoot!, sidecar.Index);
        }

        return sidecars;
    }

    /// <summary>Validates <paramref name="request"/> before anything reaches the wire, then writes it; returns the response stream, the request's total column count and its columns per root.</summary>
    /// <remarks>A repeated (root, column) is refused: the listen side serves each asked pair, and the dial side takes each pair once.</remarks>
    private async Task<(Stream Stream, int TotalRequestedColumns, Dictionary<Hash256, HashSet<ulong>> Requested)> WriteRequestAsync(IChannel downChannel, DataColumnsByRootIdentifier[] request)
    {
        if (request.Length > (int)BlocksProtocolBase.MaxRequestBlocks)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Length, $"Cannot request more than {BlocksProtocolBase.MaxRequestBlocks} block roots in a single request");
        }

        ulong totalRequestedColumns = 0;
        Dictionary<Hash256, HashSet<ulong>> requested = [];
        foreach (DataColumnsByRootIdentifier identifier in request)
        {
            int columns = identifier.Columns?.Length ?? 0;
            if (columns == 0 || columns > Eip7594DasConstants.NumberOfColumns)
            {
                throw new ArgumentOutOfRangeException(nameof(request), columns, $"Each identifier's columns must be 1..{Eip7594DasConstants.NumberOfColumns}");
            }

            totalRequestedColumns += (ulong)columns;
            if (!requested.TryGetValue(identifier.BlockRoot!, out HashSet<ulong>? rootColumns))
            {
                requested[identifier.BlockRoot!] = rootColumns = [];
            }

            foreach (ulong column in identifier.Columns!)
            {
                if (!rootColumns.Add(column))
                {
                    throw new ArgumentOutOfRangeException(nameof(request), column, $"Column {column} of block {identifier.BlockRoot} is requested more than once");
                }
            }
        }

        if (totalRequestedColumns > MaxRequestDataColumnSidecars)
        {
            throw new ArgumentOutOfRangeException(nameof(request), totalRequestedColumns, $"Cannot request more than {MaxRequestDataColumnSidecars} data column sidecars in a single request");
        }

        Stream stream = new ChannelStreamAdapter(downChannel);
        using (CancellationTokenSource cts = StartTimeout(RespTimeout))
        {
            await WriteRequestAndEofAsync(downChannel, stream, DataColumnSidecarsByRootRequest.Encode(new DataColumnSidecarsByRootRequest { Identifiers = request }), cts.Token);
        }

        return (stream, (int)totalRequestedColumns, requested);
    }

    // Removing the pair on first use is what refuses a repeated (root, column).
    private void TakeRequested(Dictionary<Hash256, HashSet<ulong>> requested, Hash256 blockRoot, ulong column)
    {
        if (!requested.TryGetValue(blockRoot, out HashSet<ulong>? columns) || !columns.Remove(column))
        {
            RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            throw new Eth2ReqRespException("Peer responded with a data column sidecar that was not requested");
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
            DataColumnSidecarsByRootRequest request;
            try
            {
                DataColumnSidecarsByRootRequest.Decode(requestSsz, out request);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Eth2ReqRespException($"Malformed data-column-sidecars-by-root request: {e.Message}");
            }

            DataColumnsByRootIdentifier[] identifiers = request.Identifiers ?? [];
            if (identifiers.Length > (int)BlocksProtocolBase.MaxRequestBlocks)
            {
                throw new Eth2ReqRespException($"Cannot request more than {BlocksProtocolBase.MaxRequestBlocks} block roots in a single request");
            }

            // Bound the total before touching the pool at all: a wide fan of identifiers each asking
            // for many columns is exactly the shape a memory-exhaustion attempt would take.
            ulong totalRequestedColumns = 0;
            foreach (DataColumnsByRootIdentifier identifier in identifiers)
            {
                int columns = identifier.Columns?.Length ?? 0;
                if (columns == 0 || columns > Eip7594DasConstants.NumberOfColumns)
                {
                    throw new Eth2ReqRespException($"Each identifier's columns must be 1..{Eip7594DasConstants.NumberOfColumns}");
                }

                totalRequestedColumns += (ulong)columns;
                if (totalRequestedColumns > MaxRequestDataColumnSidecars)
                {
                    throw new Eth2ReqRespException($"Cannot request more than {MaxRequestDataColumnSidecars} data column sidecars in a single request");
                }
            }

            foreach (DataColumnsByRootIdentifier identifier in identifiers)
            {
                foreach (ulong column in identifier.Columns!)
                {
                    if (pool.TryGet(identifier.BlockRoot!, column, out DataColumnSidecar? sidecar))
                    {
                        await WriteSidecarChunkAsync(stream, sidecar!, cts);
                    }
                    else if (pool.TryGetGloas(identifier.BlockRoot!, column, out DataColumnSidecarGloas? gloasSidecar))
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
