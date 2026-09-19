// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Types;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The Fulu eth2 <c>metadata</c> v3 protocol. The request has no body; the response is our <see cref="MetaDataV3"/>.</summary>
/// <remarks>The unused <c>ulong</c> request type parameter only satisfies the typed libp2p dial API.</remarks>
public sealed class MetaDataProtocolV3(LocalMetadataSource metadataSource) : ReqRespProtocolBase, ISessionProtocol<ulong, MetaDataV3>
{
    private const int MetaDataV3Length = 25;

    public string Id => "/eth2/beacon_chain/req/metadata/3/ssz_snappy";

    public async Task<MetaDataV3> DialAsync(IChannel downChannel, ISessionContext context, ulong request)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using CancellationTokenSource cts = StartTimeout(TtfbTimeout + RespTimeout);
        await WriteEofAsync(downChannel, cts.Token);
        ResponseChunk chunk = await ReqRespFraming.ReadResponseChunkAsync(stream, 0, MetaDataV3Length, cts.Token)
            ?? throw new Eth2ReqRespException("Peer closed the stream without responding");
        if (chunk.Result != ReqRespFraming.ResponseCode.Success)
        {
            throw ErrorChunkToException(chunk);
        }

        MetaDataV3.Decode(chunk.Payload, out MetaDataV3 metadata);
        return metadata;
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, default, MetaDataV3.Encode(metadataSource.Current), cts.Token);
    }
}
