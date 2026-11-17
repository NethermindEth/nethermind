// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.WebSockets;

internal sealed class SendLimitedPipeWriter(SocketSendLock sendLock, CountingStreamPipeWriter writer) : CountingWriter
{
    internal CountingStreamPipeWriter Inner => writer;

    internal void Sync() => WrittenCount = writer.WrittenCount;

    public override void Advance(int bytes)
    {
        sendLock.EnsureWithinUnsentLimit(writer);
        writer.Advance(bytes);
        WrittenCount = writer.WrittenCount;
        sendLock.EnsureWithinUnsentLimit(writer);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        sendLock.EnsureWithinUnsentLimit(writer);
        return writer.GetMemory(sizeHint);
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        sendLock.EnsureWithinUnsentLimit(writer);
        return writer.GetSpan(sizeHint);
    }

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        FlushResult result = await sendLock.FlushAsync(writer, cancellationToken);
        WrittenCount = writer.WrittenCount;
        return result;
    }

    public override bool CanGetUnflushedBytes => true;

    public override long UnflushedBytes => writer.UnflushedBytes;

    public override void CancelPendingFlush() => writer.CancelPendingFlush();

    public override void Complete(Exception? exception = null) => writer.Complete(exception);
}
