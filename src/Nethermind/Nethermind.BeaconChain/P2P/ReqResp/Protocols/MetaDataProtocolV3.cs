// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        using ChannelStreamAdapter input = new(downChannel);
        using CancellationTokenSource cts = StartTimeout(TtfbTimeout + RespTimeout);
        bool classified = false;
        try
        {
            ResponseChunk chunk;
            try
            {
                await WriteEofAsync(downChannel, cts.Token);
                ResponseChunk? read = await ReqRespFraming.ReadResponseChunkAsync(input, 0, MetaDataV3Length, cts.Token);
                // A clean close before any byte is the peer or this node ending the session, not a malformed response.
                classified = read is null;
                chunk = read ?? throw new Eth2ReqRespException("Peer closed without responding");
            }
            catch (Exception e) when (e is not Eth2ReqRespException && cts.IsCancellationRequested)
            {
                RecordFailure(Id, ReqRespFailureReason.Timeout);
                throw new ReqRespTimeoutException($"timed out after {Seconds(TtfbTimeout + RespTimeout)} waiting for the response", e);
            }

            if (chunk.Result != ReqRespFraming.ResponseCode.Success)
            {
                classified = true;
                RecordFailure(Id, ReqRespFailureReason.PeerError);
                throw ErrorChunkToException(chunk);
            }

            try
            {
                MetaDataV3.Decode(chunk.Payload, out MetaDataV3 metadata);
                return metadata;
            }
            catch (Exception e) when (e is not Eth2ReqRespException and not OperationCanceledException)
            {
                RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
            throw;
        }
        catch (Eth2ReqRespException) when (!classified)
        {
            RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            throw;
        }
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        await using InboundRequest? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            return;
        }

        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        try
        {
            await inboundSlot.AcceptRequestWithoutPayloadAsync(stream, cts.Token);
            await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, default, MetaDataV3.Encode(metadataSource.Current), cts.Token);
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
