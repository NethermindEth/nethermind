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
            if (delimiter != -1)
            {
                endOfMessage = true;
                _bufferedDataLength = 0;
            }
            else if (TryGetCompleteJsonMessageLength(buffer[..read], out int messageLength))
            {
                int remainingLength = read - messageLength;
                if (remainingLength > 0)
                {
                    _bufferedDataLength = remainingLength;

                    if (_bufferedData.Length < buffer.Count)
                    {
                        if (_bufferedData.Length != 0)
                            ArrayPool<byte>.Shared.Return(_bufferedData);

                        _bufferedData = ArrayPool<byte>.Shared.Rent(buffer.Count);
                    }

                    buffer[messageLength..read].CopyTo(_bufferedData);
                }
                else
                {
                    _bufferedDataLength = 0;
                }

                endOfMessage = true;
                read = messageLength;
            }
            else
            {
                endOfMessage = false;
                _bufferedDataLength = 0;
            }
        }

        return new()
        {
            Closed = read == 0,
            Read = read > 0 && buffer[read - 1] == Delimiter ? read - 1 : read,
            EndOfMessage = endOfMessage
        };
    }

    private static readonly JsonReaderOptions _jsonReaderOptions = new() { AllowMultipleValues = true };

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

        Utf8JsonReader jsonReader = new(jsonSpan, isFinalBlock: false, new JsonReaderState(_jsonReaderOptions));

        try
        {
            bool parsed = JsonDocument.TryParseValue(ref jsonReader, out JsonDocument? jsonDocument);
            jsonDocument?.Dispose();
            if (!parsed)
            {
                messageLength = 0;
                return false;
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
