// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

public class IpcSocketMessageStream(Socket socket) : NetworkStream(socket), IMessageBorderPreservingStream
{
    private static readonly byte Delimiter = Convert.ToByte('\n');

    public byte[] bufferedData = [];
    public int bufferedDataLength = 0;

    public async Task<ReceiveResult?> ReceiveAsync(ArraySegment<byte> buffer)
    {
        ReceiveResult? result = null;
        if (Socket.Connected)
        {
            if (bufferedDataLength > 0)
            {
                if (bufferedDataLength > buffer.Count)
                {
                    throw new NotSupportedException($"Passed {nameof(buffer)} should be larger than internal one");
                }
                try
                {
                    Buffer.BlockCopy(bufferedData, 0, buffer.Array!, buffer.Offset, bufferedDataLength);
                }
                catch(Exception ) 
                {

                }
            }

            int read = bufferedDataLength + await Socket.ReceiveAsync(buffer[bufferedDataLength..], SocketFlags.None);

            int delimiter = ((IList<byte>)buffer[..read]).IndexOf(Delimiter);

            bool endOfMessage;
            if (delimiter != -1 && (delimiter + 1) < read)
            {
                bufferedDataLength = read - delimiter - 1;

                if (bufferedData.Length < buffer.Count)
                {
                    if (bufferedData.Length != 0)
                    {
                        ArrayPool<byte>.Shared.Return(bufferedData);
                    }
                    bufferedData = ArrayPool<byte>.Shared.Rent(buffer.Count);
                }
                endOfMessage = true;
                buffer[(delimiter + 1)..read].CopyTo(bufferedData);
                read = delimiter + 1;
            }
            else
            {
                endOfMessage = delimiter != -1;
                bufferedDataLength = 0;
            }

            result = new ReceiveResult()
            {
                Closed = read == 0,
                Read = read > 0 && buffer[read - 1] == Delimiter ? read - 1 : read,
                EndOfMessage = endOfMessage,
                CloseStatusDescription = null
            };
        }

        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && bufferedData.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(bufferedData);
        }
        base.Dispose(disposing);
    }

    public Task<int> WriteEndOfMessageAsync()
    {
        WriteByte(Delimiter);
        return Task.FromResult(1);
    }
}
