// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Sockets
{
    public class IpcSocketsHandler : ISocketHandler
    {
        private readonly Socket _socket;

        public IpcSocketsHandler(Socket socket)
        {
            _socket = socket;
        }

        public Task SendRawAsync(ArraySegment<byte> data, bool endOfMessage) =>
            !_socket.Connected
                ? Task.CompletedTask
                : _socket.SendAsync(data, SocketFlags.None);

        public async Task<ReceiveResult?> GetReceiveResult(ArraySegment<byte> buffer)
        {
            ReceiveResult? result = null;
            if (_socket.Connected)
            {
                int read = await _socket.ReceiveAsync(buffer, SocketFlags.None);
                result = new ReceiveResult()
                {
                    Closed = read == 0,
                    Read = read,
                    EndOfMessage = read < buffer.Count || _socket.Available == 0,
                    CloseStatusDescription = null
                };
            }

            return result;
        }

        public Task CloseAsync(ReceiveResult? result)
        {
            return Task.Factory.StartNew(_socket.Close);
        }

        public Stream SendUsingStream()
        {
            return new NetworkStream(_socket, FileAccess.Write, ownsSocket: false);
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}
