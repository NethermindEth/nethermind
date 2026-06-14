// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp;

/// <summary>Exposes a libp2p <see cref="IChannel"/> as an async-only <see cref="Stream"/>.</summary>
/// <remarks>
/// Used instead of the library's <c>ChannelStream</c>, whose <c>ReadAsync(byte[], int, int)</c>
/// overload ignores the offset and count arguments and whose reads do not honor cancellation.
/// End of stream reads as 0; that includes channel teardown (<see cref="IOResult.Cancelled"/>
/// without the caller's token fired), which yamux reports for reads after both sides half-closed
/// instead of <see cref="IOResult.Ended"/> — the framing layer distinguishes a clean end from a
/// truncated message by position. Synchronous I/O is unsupported by design — the framing layer is
/// fully asynchronous.
/// </remarks>
internal sealed class ChannelStreamAdapter(IChannel channel) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        ReadResult result = await channel.ReadAsync(buffer.Length, ReadBlockingMode.WaitAny, cancellationToken);
        switch (result.Result)
        {
            case IOResult.Ended:
                return 0;
            case IOResult.Cancelled:
                cancellationToken.ThrowIfCancellationRequested();
                return 0; // The channel itself was torn down: treat as end of stream.
            case not IOResult.Ok:
                throw new IOException($"Channel read failed: {result.Result}");
        }

        result.Data.CopyTo(buffer.Span);
        return (int)result.Data.Length;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        IOResult result = await channel.WriteAsync(new ReadOnlySequence<byte>(buffer), cancellationToken);
        if (result != IOResult.Ok)
        {
            throw new IOException($"Channel write failed: {result}");
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
