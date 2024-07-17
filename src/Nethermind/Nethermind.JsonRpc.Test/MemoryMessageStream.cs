// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.Test;

public class MemoryMessageStream : MemoryStream, IMessageBorderPreservingStream
{
    private static readonly byte Delimiter = Convert.ToByte('\n');

    public Task<ReceiveResult?> ReceiveAsync(ArraySegment<byte> buffer)
    {
        int read = Read(buffer.AsSpan());
        return Task.FromResult<ReceiveResult?>(new ReceiveResult
        {
            Read = read,
            EndOfMessage = read > 0 && buffer[read - 1] == Delimiter
        });
    }

    public Task<int> WriteEndOfMessageAsync()
    {
        WriteByte(Delimiter);
        return Task.FromResult(1);
    }
}
