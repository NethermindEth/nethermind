// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using IdentifyMessage = Nethermind.Libp2p.Protocols.Identify.Dto.Identify;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// A dial-only second reading of a peer's <c>/ipfs/id/1.0.0</c> answer, for the one field the
/// pinned libp2p identify implementation reads and then discards: <c>agentVersion</c>, the client
/// string the Beacon API's <c>node/peers</c> surface reports. The library's own
/// <see cref="IdentifyProtocol"/> still runs first on every session and keeps doing the identity
/// verification and peer-store bookkeeping; this only asks the same question again and keeps the answer.
/// </summary>
/// <remarks>
/// Shares the identify protocol id, so it must never answer an inbound identify request differently
/// from the library: <see cref="ListenAsync"/> hands the stream to the real protocol instance. The
/// unused <c>ulong</c> request type only satisfies the typed libp2p dial API.
/// </remarks>
public sealed class IdentifyAgentVersionProbe(IdentifyProtocol identify) : ISessionProtocol<ulong, string?>
{
    /// <summary>Also bounds the whole probe dial in <see cref="BeaconP2P"/>: every admission waits on this answer.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    public string Id => identify.Id;

    public async Task<string?> DialAsync(IChannel downChannel, ISessionContext context, ulong request)
    {
        using CancellationTokenSource cts = new(ReadTimeout);
        IdentifyMessage message = await downChannel.ReadPrefixedProtobufAsync(IdentifyMessage.Parser, cts.Token);
        return message.HasAgentVersion ? message.AgentVersion : null;
    }

    public Task ListenAsync(IChannel downChannel, ISessionContext context) => identify.ListenAsync(downChannel, context);
}
