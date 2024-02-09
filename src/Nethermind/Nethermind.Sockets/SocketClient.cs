// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

namespace Nethermind.Sockets;

public class SocketClient<TStream> : ISocketsClient where TStream : Stream, IMessageBorderPreservingStream
{
    public const int MAX_POOLED_SIZE = 5 * 1024 * 1024;

    protected readonly TStream _stream;
    protected readonly IJsonSerializer _jsonSerializer;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string ClientName { get; }

    public SocketClient(string clientName, TStream stream, IJsonSerializer jsonSerializer)
    {
        ClientName = clientName;
        _stream = stream;
        _jsonSerializer = jsonSerializer;
    }

    public virtual Task ProcessAsync(ArraySegment<byte> data) => Task.CompletedTask;

    public virtual async Task SendAsync(SocketsMessage message)
    {
        if (message is null)
        {
            return;
        }

        if (message.Client == ClientName || string.IsNullOrWhiteSpace(ClientName) ||
            string.IsNullOrWhiteSpace(message.Client))
        {
            await _jsonSerializer.SerializeAsync(_stream, new { type = message.Type, client = ClientName, data = message.Data }, indented: false, leaveOpen: true);
            await _stream.WriteEndOfMessageAsync();
        }
    }

    public async Task ReceiveLoopAsync()
    {
        const int standardBufferLength = 1024 * 4;
        int currentMessageLength = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(standardBufferLength);
        try
        {
            ReceiveResult? result = await _stream.ReceiveAsync(buffer);
            while (result?.Closed == false)
            {
                currentMessageLength += result.Read;

                if (currentMessageLength >= MAX_POOLED_SIZE)
                {
                    throw new InvalidOperationException("Message too long");
                }

                if (result.EndOfMessage)
                {
                    // process the already filled bytes
                    await ProcessAsync(new ArraySegment<byte>(buffer, 0, currentMessageLength));
                    currentMessageLength = 0; // reset message length

                    // if we grew the buffer too big lets reset it
                    if (buffer.Length > 2 * standardBufferLength)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = ArrayPool<byte>.Shared.Rent(standardBufferLength);
                    }
                }
                else if (buffer.Length - currentMessageLength < standardBufferLength) // there is little room in current buffer
                {
                    // grow the buffer 4x, but not more than max
                    int newLength = Math.Min(buffer.Length * 4, MAX_POOLED_SIZE);
                    if (newLength > buffer.Length)
                    {
                        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newLength);
                        buffer.CopyTo(newBuffer, 0);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = newBuffer;
                    }
                }

                // receive only new bytes, leave already filled buffer alone
                result = await _stream.ReceiveAsync(new ArraySegment<byte>(buffer, currentMessageLength, buffer.Length - currentMessageLength));
            }
        }
        finally
        {
            await _stream.DisposeAsync();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public virtual void Dispose()
    {
        _stream?.Dispose();
    }
}
