// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp;

/// <summary>Adds request-tail and half-close operations to a non-owning channel stream.</summary>
/// <remarks>The protocol handler owns the channel lifetime; disposing this stream leaves it open.</remarks>
internal sealed class ChannelStreamAdapter(IChannel channel) : ChannelStream(channel, leaveOpen: true)
{
    private readonly IChannel _channel = channel;

    /// <summary>Reads one byte only if the transport already holds it; never waits for one to arrive.</summary>
    /// <returns><c>false</c> when no byte is buffered or the stream has ended.</returns>
    internal async ValueTask<bool> TryReadBufferedByteAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        ReadResult result = await _channel.ReadAsync(1, ReadBlockingMode.DoNotWait, cancellationToken);
        if (result.Result != IOResult.Ok || result.Data.Length == 0)
        {
            return false;
        }

        result.Data.Slice(0, 1).CopyTo(buffer);
        return true;
    }

    /// <summary>Half-closes the write side, which ends the response for the reader while this side keeps reading.</summary>
    /// <remarks>A channel that is already closed, or a token that fires first, only means there is nothing left to end.</remarks>
    internal async ValueTask TryWriteEofAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _channel.WriteEofAsync(cancellationToken);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Waits for transport closure without consuming any more request bytes.</summary>
    internal Task WaitForCloseAsync(CancellationToken cancellationToken) => ClosedAsync().WaitAsync(cancellationToken);
    private async Task ClosedAsync() => await _channel;
}
