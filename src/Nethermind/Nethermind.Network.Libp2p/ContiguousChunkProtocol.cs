// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace Nethermind.Network.Libp2p;

/// <summary>Sits between yamux and multistream on every yamux channel and hands each received chunk upward as one contiguous segment.</summary>
/// <remarks>
/// Nethermind.Libp2p 1.0.0 <c>Channel.ReadAsync</c> keeps only the first segment of every chunk after the first when it gathers an exact
/// length, and a yamux frame read from Noise arrives as several segments, so a gossip RPC spanning frames reached pubsub truncated and
/// cost the peer. Copying a multi-segment chunk into one array makes every later chunk a single segment. Data sent down passes unchanged.
/// </remarks>
public sealed class ContiguousChunkProtocol : IConnectionProtocol
{
    /// <summary>Never negotiated: the stack connects it directly after yamux.</summary>
    public string Id => "/nethermind/contiguous-chunks";

    public Task ListenAsync(IChannel downChannel, IConnectionContext context) => RelayAsync(downChannel, context.Upgrade());

    public Task DialAsync(IChannel downChannel, IConnectionContext context) => RelayAsync(downChannel, context.Upgrade());

    /// <summary>Relays both directions between <paramref name="lower"/> (yamux) and <paramref name="upper"/> (multistream) until both have ended.</summary>
    /// <remarks>Each direction forwards its end of data on its own, so a half-closed request keeps receiving its response. A full close of
    /// either side closes the other, so a channel the protocol above abandons does not stay open below while the peer keeps it open.</remarks>
    internal static async Task RelayAsync(IChannel lower, IChannel upper)
    {
        Task pumps = Task.WhenAll(PumpAsync(lower, upper, contiguous: true), PumpAsync(upper, lower, contiguous: false));
        if (await Task.WhenAny(pumps, ClosedAsync(lower), ClosedAsync(upper)) != pumps)
        {
            await lower.CloseAsync();
            await upper.CloseAsync();
        }

        await pumps;
    }

    private static async Task ClosedAsync(IChannel channel) => await channel;

    private static async Task PumpAsync(IChannel from, IChannel to, bool contiguous)
    {
        while (true)
        {
            ReadResult read = await from.ReadAsync(0, ReadBlockingMode.WaitAny);
            if (read.Result != IOResult.Ok)
            {
                if (read.Result == IOResult.Ended)
                {
                    await to.WriteEofAsync();
                }
                else
                {
                    await to.CloseAsync();
                }

                return;
            }

            ReadOnlySequence<byte> data = contiguous && !read.Data.IsSingleSegment ? new ReadOnlySequence<byte>(read.Data.ToArray()) : read.Data;
            if (await to.WriteAsync(data) != IOResult.Ok)
            {
                await from.CloseAsync();
                return;
            }
        }
    }
}
