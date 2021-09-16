using System;
using System.Collections.Generic;
using System.Linq;
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

        public Task SendRawAsync(ArraySegment<byte> data) =>
            !_socket.Connected
                ? Task.CompletedTask
                : _socket.SendAsync(data, SocketFlags.None);

        public async Task<ReceiveResult?> GetReceiveResult(byte[] buffer)
        {
            int read = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
            return new ReceiveResult()
            {
                Closed = read == 0,
                Read = read,
                EndOfMessage = read < buffer.Length || _socket.Available == 0,
                CloseStatusDescription = null
            };
        }

        public Task CloseAsync(ReceiveResult? result)
        {
            return Task.Factory.StartNew(_socket.Close);
        }

        public void Dispose()
        {
            _socket?.Dispose();
        }
    }
}
