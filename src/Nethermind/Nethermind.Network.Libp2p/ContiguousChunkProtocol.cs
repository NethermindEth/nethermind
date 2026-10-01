// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace Nethermind.Network.Libp2p;

/// <summary>Sits between yamux and multistream on every stream and hands each received chunk upward as one contiguous segment.</summary>
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
    /// <remarks>Each direction forwards its end of stream on its own, so a half-closed request keeps receiving its response.</remarks>
    internal static Task RelayAsync(IChannel lower, IChannel upper) =>
        Task.WhenAll(PumpAsync(lower, upper, contiguous: true), PumpAsync(upper, lower, contiguous: false));

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
