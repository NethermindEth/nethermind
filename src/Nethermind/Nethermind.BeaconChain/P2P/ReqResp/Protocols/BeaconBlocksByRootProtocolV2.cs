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

/// <summary>The eth2 <c>beacon_blocks_by_root</c> v2 protocol.</summary>
/// <remarks>
/// The listen side serves whichever requested blocks the local store has, skipping unknown roots.
/// The dial side recomputes each returned block's hash tree root and rejects blocks that were not asked for.
/// </remarks>
public sealed class BeaconBlocksByRootProtocolV2(BeaconChainSpec spec, BeaconChainStore store) : BlocksProtocolBase(spec),
    ISessionProtocol<Hash256[], IReadOnlyList<SignedBeaconBlock>>
{
    private const int MaxRequestLength = (int)MaxRequestBlocks * Hash256.Size;

    public string Id => "/eth2/beacon_chain/req/beacon_blocks_by_root/2/ssz_snappy";

    public async Task<IReadOnlyList<SignedBeaconBlock>> DialAsync(IChannel downChannel, ISessionContext context, Hash256[] request)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using (CancellationTokenSource cts = StartTimeout(RespTimeout))
        {
            await WriteRequestAndEofAsync(downChannel, stream, BeaconBlocksByRootRequest.Encode(new BeaconBlocksByRootRequest { Roots = request }), cts.Token);
        }

        IReadOnlyList<SignedBeaconBlock> blocks = await ReadBlockChunksAsync(stream, request.Length);
        HashSet<Hash256> requestedRoots = [.. request];
        foreach (SignedBeaconBlock block in blocks)
        {
            if (!requestedRoots.Remove(SszRoots.HashTreeRoot(block.Message!)))
            {
                throw new Eth2ReqRespException("Peer responded with a block that was not requested");
            }
        }

        return blocks;
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        try
        {
            byte[] requestSsz = await ReqRespFraming.ReadRequestAsync(stream, MaxRequestLength, cts.Token);
            BeaconBlocksByRootRequest request;
            try
            {
                BeaconBlocksByRootRequest.Decode(requestSsz, out request);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Eth2ReqRespException($"Malformed blocks-by-root request: {e.Message}");
            }

            foreach (Hash256 root in request.Roots ?? [])
            {
                if (store.TryGetBlock(root, out SignedBeaconBlock? block))
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
