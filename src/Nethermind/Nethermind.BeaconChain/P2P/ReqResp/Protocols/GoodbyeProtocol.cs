// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
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
/// <remarks>The listener answers with one success chunk echoing the reason (p2p-interface Goodbye); the dial side does not await it before disconnecting.</remarks>
public sealed class GoodbyeProtocol : SingleChunkProtocol<ulong, ulong>
{
    public override string Id => "/eth2/beacon_chain/req/goodbye/1/ssz_snappy";

    protected override int MaxRequestSize => sizeof(ulong);
    protected override int MaxResponseSize => sizeof(ulong);
    protected override byte[] EncodeRequest(ulong request) => Eth2PingProtocol.EncodeUint64(request);
    protected override ulong DecodeRequest(byte[] ssz) => Eth2PingProtocol.DecodeUint64(ssz);
    protected override byte[] EncodeResponse(ulong response) => Eth2PingProtocol.EncodeUint64(response);
    protected override ulong DecodeResponse(byte[] ssz) => Eth2PingProtocol.DecodeUint64(ssz);
    protected override ulong HandleRequest(ulong request) => request;

    public override async Task<ulong> DialAsync(IChannel downChannel, ISessionContext context, ulong request)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        await WriteRequestAndEofAsync(downChannel, stream, Eth2PingProtocol.EncodeUint64(request), cts.Token);
        return request;
    }
}
