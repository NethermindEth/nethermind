// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

public class IpcSocketMessageStream(Socket socket) : NetworkStream(socket), IMessageBorderPreservingStream
{
    private static readonly byte[] NewLine = [Convert.ToByte('\n')];
    public async Task<ReceiveResult?> ReceiveAsync(ArraySegment<byte> buffer)
    {
        ReceiveResult? result = null;
        if (Socket.Connected)
        {
            bool endOfMessage = false;
            int read = await Socket.ReceiveAsync(buffer, SocketFlags.Peek);

            int delimiter;
            if (read < buffer.Count || buffer[^1] == NewLine[0])
            {
                endOfMessage = read > 0 && buffer[read - 1 ] == NewLine[0];
                await Socket.ReceiveAsync(buffer, SocketFlags.None);
            }
            else if ((delimiter = ((IList<byte>)buffer).IndexOf(NewLine[0])) != -1)
            {
                read = await Socket.ReceiveAsync(buffer[0..delimiter], SocketFlags.None);
                endOfMessage = true;
            }
            else
            {
                await Socket.ReceiveAsync(buffer, SocketFlags.None);
            }
            result = new ReceiveResult()
            {
                Closed = read == 0,
                Read = read,
                EndOfMessage = endOfMessage,
                CloseStatusDescription = null
            };
        }

        return result;
    }

    async Task<int> IMessageBorderPreservingStream.WriteEndOfMessageAsync()
    {
        await WriteAsync(NewLine);
        return 1;
    }
}
