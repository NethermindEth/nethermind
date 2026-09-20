// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Well-known eth2 goodbye reason codes.</summary>
/// <remarks>
/// 1-3 are the base spec's codes. The p2p spec reserves codes >= 128 for client-defined extensions;
/// <see cref="TooManyPeers"/> and <see cref="Banned"/> reuse Lighthouse's own values for those
/// (sigp/lighthouse <c>rpc/methods.rs</c> <c>GoodbyeReason</c>) so a Lighthouse peer logs a reason it
/// already recognises instead of an opaque number.
/// </remarks>
public static class GoodbyeReason
{
    public const ulong ClientShutdown = 1;
    public const ulong IrrelevantNetwork = 2;
    public const ulong Fault = 3;
    public const ulong TooManyPeers = 129;
    public const ulong Banned = 251;
}

/// <summary>The eth2 <c>goodbye</c> protocol.</summary>
/// <remarks>Fire-and-forget on the dial side: like other clients, no response chunk is awaited before disconnecting.</remarks>
public sealed class GoodbyeProtocol : ReqRespProtocolBase, ISessionProtocol<ulong, ulong>
{
    public string Id => "/eth2/beacon_chain/req/goodbye/1/ssz_snappy";

    public async Task<ulong> DialAsync(IChannel downChannel, ISessionContext context, ulong request)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        await WriteRequestAndEofAsync(downChannel, stream, Eth2PingProtocol.EncodeUint64(request), cts.Token);
        return request;
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using IDisposable? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            return;
        }

        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        try
        {
            Eth2PingProtocol.DecodeUint64(await ReqRespFraming.ReadRequestAsync(stream, sizeof(ulong), cts.Token));
        }
        catch (Eth2ReqRespException)
        {
            RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
        }
    }
}
