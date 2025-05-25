// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.Test;

public class MemoryMessageStream : MemoryStream, IMessageBorderPreservingStream
{
    private static readonly byte Delimiter = Convert.ToByte('\n');

    public ValueTask<ReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = Read(buffer.AsSpan());
        return ValueTask.FromResult(new ReceiveResult
        {
            Read = read,
            EndOfMessage = read > 0 && buffer[read - 1] == Delimiter
        });
    }

    public ValueTask<int> WriteEndOfMessageAsync()
    {
        WriteByte(Delimiter);
        return ValueTask.FromResult(1);
    }
}
