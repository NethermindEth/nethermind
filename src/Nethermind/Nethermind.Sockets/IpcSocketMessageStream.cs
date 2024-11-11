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
    private const byte Delimiter = (byte)'\n';

    private byte[] _bufferedData = [];
    private int _bufferedDataLength = 0;

    public async Task<ReceiveResult?> ReceiveAsync(ArraySegment<byte> buffer)
    {
        if (!Socket.Connected)
            return null;

        if (_bufferedDataLength > 0)
        {
            if (_bufferedDataLength > buffer.Count)
                throw new NotSupportedException($"Passed {nameof(buffer)} should be larger than internal one");

            try
            {
                Buffer.BlockCopy(_bufferedData, 0, buffer.Array!, buffer.Offset, _bufferedDataLength);
            }
            catch { }
        }

        int read = _bufferedDataLength + await Socket.ReceiveAsync(buffer[_bufferedDataLength..], SocketFlags.None);

        int delimiter = ((IList<byte>)buffer[..read]).IndexOf(Delimiter);
        bool endOfMessage;

        if (delimiter != -1 && (delimiter + 1) < read)
        {
            _bufferedDataLength = read - delimiter - 1;

            if (_bufferedData.Length < buffer.Count)
            {
                if (_bufferedData.Length != 0)
                    ArrayPool<byte>.Shared.Return(_bufferedData);

                _bufferedData = ArrayPool<byte>.Shared.Rent(buffer.Count);
            }

            endOfMessage = true;
            buffer[(delimiter + 1)..read].CopyTo(_bufferedData);
            read = delimiter + 1;
        }
        else
        {
            endOfMessage = delimiter != -1 || Socket.Available == 0;
            _bufferedDataLength = 0;
        }

        return new()
        {
            Closed = read == 0,
            Read = read > 0 && buffer[read - 1] == Delimiter ? read - 1 : read,
            EndOfMessage = endOfMessage,
            CloseStatusDescription = null
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _bufferedData.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(_bufferedData);
        }
        base.Dispose(disposing);
    }

    public Task<int> WriteEndOfMessageAsync()
    {
        WriteByte(Delimiter);
        return Task.FromResult(1);
    }
}
