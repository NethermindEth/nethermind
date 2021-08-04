using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

namespace Nethermind.Sockets
{
    public class SocketClient : ISocketsClient
    {
        public const int MAX_POOLED_SIZE = 1024 * 1024;

        protected readonly ISocketHandler _handler;
        protected readonly IJsonSerializer _jsonSerializer;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string ClientName { get; }

        public SocketClient(string clientName, ISocketHandler handler, IJsonSerializer jsonSerializer)
        {
            ClientName = clientName;
            _handler = handler;
            _jsonSerializer = jsonSerializer;
        }

        public virtual Task ProcessAsync(Memory<byte> data) => Task.CompletedTask;

        public virtual Task SendAsync(SocketsMessage message)
        {
            if (message is null)
            {
                return Task.CompletedTask;
            }

            if (message.Client == ClientName || string.IsNullOrWhiteSpace(ClientName) ||
                string.IsNullOrWhiteSpace(message.Client))
            {
                return _handler.SendRawAsync(_jsonSerializer.Serialize(new
                {
                    type = message.Type,
                    client = ClientName,
                    data = message.Data
                }));
            }

            return Task.CompletedTask;
        }

        public async Task ReceiveAsync()
        {
            int currentMessageLength = 0;
            byte[] buffer = new byte[1024 * 4];
            byte[] combinedData = Array.Empty<byte>();

            var result = await _handler.GetReceiveResult(buffer);
            if (result == null)
            {
                return;
            }

            while (!result.Closed)
            {
                int newMessageLength = currentMessageLength + result.Read;
                if (newMessageLength > MAX_POOLED_SIZE)
                {
                    throw new InvalidOperationException("Message too long");
                }

                byte[] newBytes = ArrayPool<byte>.Shared.Rent(newMessageLength);
                try
                {
                    buffer.AsSpan(0, result.Read).CopyTo(newBytes.AsSpan(currentMessageLength, result.Read));
                    if (!ReferenceEquals(combinedData, Array.Empty<byte>()))
                    {
                        combinedData.AsSpan(0, currentMessageLength).CopyTo(newBytes.AsSpan(0, currentMessageLength));
                    }

                    combinedData = newBytes;
                    currentMessageLength = newMessageLength;

                    if (result.EndOfMessage)
                    {
                        Memory<byte> data = combinedData.AsMemory(0, currentMessageLength);
                        await ProcessAsync(data);
                        currentMessageLength = 0;
                        combinedData = Array.Empty<byte>();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(combinedData);
                }

                result = await _handler.GetReceiveResult(buffer);
                if (result == null)
                {
                    return;
                }
            }

            await _handler.CloseAsync(result);
        }

        public virtual void Dispose()
        {
            _handler?.Dispose();
        }
    }
}
