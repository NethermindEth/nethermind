// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

public class IpcSocketMessageStream(Socket socket) : NetworkStream(socket), IMessageBorderPreservingStream
{
    private const byte Delimiter = (byte)'\n';
    private static readonly SearchValues<byte> Whitespace = SearchValues.Create(" \n\r\t"u8);

    private byte[] _bufferedData = [];
    private int _bufferedDataLength;

    public async ValueTask<ReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Socket.Connected)
            return default;

        int read = RestoreBufferedData(buffer);

        // Find message boundary — try buffered data first to avoid blocking on socket
        (int contentLength, int consumed) = FindMessageEnd(GetFullBufferSpan(buffer, read), buffer.Offset);
        if (consumed == 0)
        {
            read += await Socket.ReceiveAsync(buffer[read..], SocketFlags.None, cancellationToken);
            (contentLength, consumed) = FindMessageEnd(GetFullBufferSpan(buffer, read), buffer.Offset);
        }

        if (consumed > 0)
        {
            SaveOverflow(buffer, consumed, read);
            return new() { Read = contentLength, EndOfMessage = true };
        }

        _bufferedDataLength = 0;
        return new() { Closed = read == 0, Read = read, EndOfMessage = false };

        int RestoreBufferedData(ArraySegment<byte> buf) => _bufferedDataLength switch
        {
            <= 0 => 0,
            _ when _bufferedDataLength > buf.Count => throw new NotSupportedException($"Passed {nameof(buf)} should be larger than internal one"),
            _ => Copy(buf)
        };

        int Copy(ArraySegment<byte> buf)
        {
            Buffer.BlockCopy(_bufferedData, 0, buf.Array!, buf.Offset, _bufferedDataLength);
            return _bufferedDataLength;
        }

        void SaveOverflow(ArraySegment<byte> buf, int start, int end)
        {
            int overflow = Math.Max(0, end - start);
            _bufferedDataLength = overflow;

            if (overflow > 0)
            {
                EnsureBufferedDataCapacity(overflow);
                buf[start..end].CopyTo(_bufferedData);
            }
        }

        static Span<byte> GetFullBufferSpan(ArraySegment<byte> buffer, int read) => buffer.Array.AsSpan(0, buffer.Offset + read);
    }

    private static (int ContentLength, int Consumed) FindMessageEnd(ReadOnlySpan<byte> data, int offset) =>
        offset >= data.Length
            ? default
            : data[offset..].IndexOf(Delimiter) switch
            {
                // Fast path: newline delimiter (only search new data)
                var i and >= 0 => (i, i + 1),
                // Slow path: complete JSON object/array (parse full accumulated data)
                _ when TryGetCompleteJsonLength(data, out int jsonLength) => (jsonLength - offset, jsonLength - offset),
                _ => default
            };

    private static bool TryGetCompleteJsonLength(ReadOnlySpan<byte> span, out int messageLength)
    {
        int offset = GetStartingOffset(span);
        ReadOnlySpan<byte> json = span[offset..];
        if (StartsWithObject(json))
        {
            Utf8JsonReader reader = new(json, isFinalBlock: false, default);
            try
            {
                if (reader.Read() && reader.TrySkip())
                {
                    messageLength = offset + (int)reader.BytesConsumed;
                    return true;
                }
            }
            catch (JsonException) { }
        }

        messageLength = 0;
        return false;

        static int GetStartingOffset(ReadOnlySpan<byte> span)
        {
            int i = span.IndexOfAnyExcept(Whitespace);
            return i < 0 ? span.Length : i;
        }

        static bool StartsWithObject(ReadOnlySpan<byte> span) => !span.IsEmpty && span[0] is (byte)'{' or (byte)'[';
    }

    private void EnsureBufferedDataCapacity(int requiredLength)
    {
        if (_bufferedData.Length < requiredLength)
        {
            if (_bufferedData.Length != 0)
            {
                ArrayPool<byte>.Shared.Return(_bufferedData);
            }

            _bufferedData = ArrayPool<byte>.Shared.Rent(requiredLength);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _bufferedData.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(_bufferedData);
        }

        base.Dispose(disposing);
    }

    public ValueTask<int> WriteEndOfMessageAsync()
    {
        WriteByte(Delimiter);
        return ValueTask.FromResult(1);
    }
}
