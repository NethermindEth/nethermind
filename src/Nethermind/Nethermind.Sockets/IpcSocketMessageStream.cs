// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Sockets;

public class IpcSocketMessageStream(Socket socket) : NetworkStream(socket), IMessageBorderPreservingStream
{
    private const byte Delimiter = (byte)'\n';

    private byte[] _bufferedData = [];
    private int _bufferedDataLength = 0;

    public async ValueTask<ReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Socket.Connected)
            return default;

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

        int delimiter = buffer[.._bufferedDataLength].AsSpan().IndexOf(Delimiter);
        int read;
        if (delimiter == -1)
        {
            if (_bufferedDataLength > 0 && TryGetCompleteJsonMessageLength(buffer[.._bufferedDataLength], out _))
            {
                read = _bufferedDataLength;
            }
            else
            {
                read = _bufferedDataLength + await Socket.ReceiveAsync(buffer[_bufferedDataLength..], SocketFlags.None, cancellationToken);
                delimiter = ((IList<byte>)buffer[..read]).IndexOf(Delimiter);
            }
        }
        else
        {
            read = _bufferedDataLength;
        }

        bool endOfMessage;

        if (delimiter != -1 && (delimiter + 1) < read)
        {
            _bufferedDataLength = read - delimiter - 1;
            EnsureBufferedDataCapacity(_bufferedDataLength);

            endOfMessage = true;
            buffer[(delimiter + 1)..read].CopyTo(_bufferedData);
            read = delimiter + 1;
        }
        else
        {
            if (delimiter != -1)
            {
                endOfMessage = true;
                _bufferedDataLength = 0;
            }
            else
            {
                int parsedBufferOffset = buffer.Offset;
                int parsedBufferLength = parsedBufferOffset + read;
                ArraySegment<byte> parseBuffer = buffer;
                if (parsedBufferOffset != 0)
                {
                    parseBuffer = new ArraySegment<byte>(buffer.Array!, 0, parsedBufferLength);
                }

                if (TryGetCompleteJsonMessageLength(parseBuffer[..parsedBufferLength], out int messageLength))
                {
                    int remainingLength = parsedBufferLength - messageLength;
                    if (remainingLength > 0)
                    {
                        _bufferedDataLength = remainingLength;
                        EnsureBufferedDataCapacity(_bufferedDataLength);
                        parseBuffer[messageLength..parsedBufferLength].CopyTo(_bufferedData);
                    }
                    else
                    {
                        _bufferedDataLength = 0;
                    }

                    endOfMessage = true;
                    read = messageLength - parsedBufferOffset;
                }
                else
                {
                    endOfMessage = false;
                    _bufferedDataLength = 0;
                }
            }
        }

        return new()
        {
            Closed = read == 0,
            Read = read > 0 && buffer[read - 1] == Delimiter ? read - 1 : read,
            EndOfMessage = endOfMessage
        };
    }

    private static bool TryGetCompleteJsonMessageLength(ArraySegment<byte> buffer, out int messageLength)
    {
        ReadOnlySpan<byte> span = buffer.AsSpan();
        int leadingWhitespaceLength = 0;
        while (leadingWhitespaceLength < span.Length && IsJsonWhitespace(span[leadingWhitespaceLength]))
        {
            leadingWhitespaceLength++;
        }

        if (leadingWhitespaceLength == span.Length)
        {
            messageLength = 0;
            return false;
        }

        ReadOnlySpan<byte> jsonSpan = span[leadingWhitespaceLength..];
        byte firstToken = jsonSpan[0];
        if (firstToken != (byte)'{' && firstToken != (byte)'[')
        {
            messageLength = 0;
            return false;
        }

        Utf8JsonReader jsonReader = new(jsonSpan, isFinalBlock: false, default);

        try
        {
            if (!jsonReader.Read())
            {
                messageLength = 0;
                return false;
            }

            if (jsonReader.TokenType is not JsonTokenType.StartObject and not JsonTokenType.StartArray)
            {
                messageLength = 0;
                return false;
            }

            int depth = 1;
            while (depth > 0)
            {
                if (!jsonReader.Read())
                {
                    messageLength = 0;
                    return false;
                }

                if (jsonReader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    depth++;
                }
                else if (jsonReader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    depth--;
                }
            }

            messageLength = leadingWhitespaceLength + (int)jsonReader.BytesConsumed;
            return true;
        }
        catch (JsonException)
        {
            // Incomplete JSON can throw here (for example truncated strings),
            // so keep buffering until we can parse a full value.
            messageLength = 0;
            return false;
        }
    }

    private static bool IsJsonWhitespace(byte value) =>
        value == (byte)' ' || value == (byte)'\n' || value == (byte)'\r' || value == (byte)'\t';

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
