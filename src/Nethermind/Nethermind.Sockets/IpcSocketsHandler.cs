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

        public Task SendRawAsync(string data)
        {
            if (!_socket.Connected)
            {
                return Task.CompletedTask;
            }
            var bytes = Encoding.UTF8.GetBytes(data);
            return _socket.SendAsync(new ArraySegment<byte>(bytes), SocketFlags.None);
        }

        public async Task<ReceiveResult> GetReceiveResult(byte[] buffer)
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

        public async Task CloseAsync(ReceiveResult result)
        {
            await Task.Factory.StartNew(_socket.Close);
        }

        public void Dispose()
        {
            _socket?.Dispose();
        }
    }
}
