// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Well-known eth2 goodbye reason codes.</summary>
public static class GoodbyeReason
{
    public const ulong ClientShutdown = 1;
    public const ulong IrrelevantNetwork = 2;
    public const ulong Fault = 3;
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
        using CancellationTokenSource cts = StartTimeout(RespTimeout);
        Eth2PingProtocol.DecodeUint64(await ReqRespFraming.ReadRequestAsync(stream, sizeof(ulong), cts.Token));
    }
}
