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
            
            int delimiter = ((IList<byte>)buffer[..read]).IndexOf(NewLine[0]);

            if (delimiter != -1)
            {
                endOfMessage = true;
                read = delimiter + 1;
            }

            await Socket.ReceiveAsync(buffer[..read], SocketFlags.None);

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
