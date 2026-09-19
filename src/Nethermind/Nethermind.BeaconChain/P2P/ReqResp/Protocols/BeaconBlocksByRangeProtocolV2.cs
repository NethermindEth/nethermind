// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The eth2 <c>beacon_blocks_by_range</c> v2 protocol.</summary>
/// <remarks>
/// The listen side serves canonical blocks from the local <see cref="BeaconChainStore"/>, skipping
/// empty slots, and answers <c>ResourceUnavailable</c> for ranges older than the checkpoint anchor.
/// The dial side validates per-chunk fork-digest context bytes and strictly increasing slots within
/// the requested range.
/// </remarks>
public sealed class BeaconBlocksByRangeProtocolV2(BeaconChainSpec spec, BeaconChainStore store) : BlocksProtocolBase(spec),
    ISessionProtocol<BeaconBlocksByRangeRequest, IReadOnlyList<SignedBeaconBlock>>
{
    private const int RequestLength = 3 * sizeof(ulong);

    public string Id => "/eth2/beacon_chain/req/beacon_blocks_by_range/2/ssz_snappy";

    public async Task<IReadOnlyList<SignedBeaconBlock>> DialAsync(IChannel downChannel, ISessionContext context, BeaconBlocksByRangeRequest request)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using (CancellationTokenSource cts = StartTimeout(RespTimeout))
        {
            await WriteRequestAndEofAsync(downChannel, stream, BeaconBlocksByRangeRequest.Encode(request), cts.Token);
        }

        IReadOnlyList<SignedBeaconBlock> blocks = await ReadBlockChunksAsync(stream, (int)Math.Min(request.Count, MaxRequestBlocks));
        ulong? previousSlot = null;
        foreach (SignedBeaconBlock block in blocks)
        {
            ulong slot = block.Message!.Slot;
            if (slot < request.StartSlot || slot >= request.StartSlot + request.Count || slot <= previousSlot)
            {
                throw new Eth2ReqRespException($"Block slot {slot} outside the requested range or out of order");
            }

            previousSlot = slot;
        }

        return blocks;
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        try
        {
            byte[] requestSsz = await ReqRespFraming.ReadRequestAsync(stream, RequestLength, cts.Token);
            if (requestSsz.Length != RequestLength)
            {
                throw new Eth2ReqRespException($"Blocks-by-range request must be {RequestLength} bytes, got {requestSsz.Length}");
            }

            BeaconBlocksByRangeRequest.Decode(requestSsz, out BeaconBlocksByRangeRequest request);
            if (request.Count == 0)
            {
                throw new Eth2ReqRespException("Blocks-by-range request count must be positive");
            }

            if (!store.TryGetAnchor(out _, out ulong anchorSlot) || request.StartSlot < anchorSlot)
            {
                throw new Eth2ReqRespException("Requested range predates the checkpoint anchor", ReqRespFraming.ResponseCode.ResourceUnavailable);
            }

            // Per the spec, step is deprecated and the request is served as if it were 1.
            ulong count = Math.Min(request.Count, MaxRequestBlocks);
            for (ulong slot = request.StartSlot; slot < request.StartSlot + count; slot++)
            {
                if (store.TryGetCanonicalRoot(slot, out Hash256? root) && store.TryGetBlock(root, out SignedBeaconBlock? block))
                {
                    await WriteBlockChunkAsync(stream, block, cts);
                }
            }
        }
        catch (Eth2ReqRespException e)
        {
            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
    }
}
