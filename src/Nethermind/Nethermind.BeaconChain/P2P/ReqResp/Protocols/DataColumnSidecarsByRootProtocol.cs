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

/// <summary>The Fulu <c>data_column_sidecars_by_root</c> v1 protocol.</summary>
/// <remarks>
/// The listen side serves whichever requested (root, column) pairs the local
/// <see cref="DataColumnSidecarPool"/> has. The dial side recomputes each returned sidecar's block
/// root from its header and rejects sidecars whose (root, column) was not requested.
/// </remarks>
public sealed class DataColumnSidecarsByRootProtocol(BeaconChainSpec spec, DataColumnSidecarPool pool) : DataColumnSidecarsProtocolBase(spec),
    ISessionProtocol<DataColumnsByRootIdentifier[], IReadOnlyList<DataColumnSidecar>>
{
    /// <summary>Fixed part per identifier (a 32-byte root + a 4-byte list offset) plus its variable columns list, bounded by NUMBER_OF_COLUMNS.</summary>
    private const int MaxIdentifierBytes = 32 /* Hash256.Size */ + 4 + Eip7594DasConstants.NumberOfColumns * sizeof(ulong);

    /// <summary>An upper bound for framing (identifiers list capped at MAX_REQUEST_BLOCKS_DENEB), not an exact length.</summary>
    private const int MaxRequestLength = (int)BlocksProtocolBase.MaxRequestBlocks * MaxIdentifierBytes;

    public string Id => "/eth2/beacon_chain/req/data_column_sidecars_by_root/1/ssz_snappy";

    public async Task<IReadOnlyList<DataColumnSidecar>> DialAsync(IChannel downChannel, ISessionContext context, DataColumnsByRootIdentifier[] request)
    {
        if (request.Length > (int)BlocksProtocolBase.MaxRequestBlocks)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Length, $"Cannot request more than {BlocksProtocolBase.MaxRequestBlocks} block roots in a single request");
        }

        ulong totalRequestedColumns = 0;
        foreach (DataColumnsByRootIdentifier identifier in request)
        {
            int columns = identifier.Columns?.Length ?? 0;
            if (columns == 0 || columns > Eip7594DasConstants.NumberOfColumns)
            {
                throw new ArgumentOutOfRangeException(nameof(request), columns, $"Each identifier's columns must be 1..{Eip7594DasConstants.NumberOfColumns}");
            }

            totalRequestedColumns += (ulong)columns;
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

        IReadOnlyList<DataColumnSidecar> sidecars = await ReadSidecarChunksAsync(stream, (int)totalRequestedColumns, Id);

        Dictionary<Hash256, HashSet<ulong>> requested = [];
        foreach (DataColumnsByRootIdentifier identifier in request)
        {
            requested[identifier.BlockRoot!] = [.. identifier.Columns!];
        }

        foreach (DataColumnSidecar sidecar in sidecars)
        {
            Hash256 blockRoot = SszRoots.HashTreeRoot(sidecar.SignedBlockHeader!.Message!);
            if (!requested.TryGetValue(blockRoot, out HashSet<ulong>? columns) || !columns.Remove(sidecar.Index))
            {
                RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
                throw new Eth2ReqRespException("Peer responded with a data column sidecar that was not requested");
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
