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

    private PooledBuffer _overflow = new();
    private JsonParseState _jsonParseState = new();

    public async ValueTask<ReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Socket.Connected)
        {
            return default;
        }

        int read = _overflow.CopyTo(buffer);

        // Find message boundary — try buffered data first to avoid blocking on socket
        (int contentLength, int consumed) = FindMessageEnd(FullBufferSpan(buffer, read), buffer.Offset);
        if (consumed == 0 && read < buffer.Count)
        {
            read += await Socket.ReceiveAsync(buffer[read..], SocketFlags.None, cancellationToken);
            (contentLength, consumed) = FindMessageEnd(FullBufferSpan(buffer, read), buffer.Offset);
        }

        if (consumed > 0)
        {
            ResetJsonParseState();
            _overflow.Append(buffer.AsSpan()[consumed..read]);
            return new() { Read = contentLength, EndOfMessage = true };
        }

        return new() { Closed = read == 0, Read = read, EndOfMessage = false };

        static Span<byte> FullBufferSpan(ArraySegment<byte> buffer, int read) => buffer.Array.AsSpan(0, buffer.Offset + read);
    }

    private (int ContentLength, int Consumed) FindMessageEnd(ReadOnlySpan<byte> data, int offset)
    {
        if (offset >= data.Length)
            return default;

        // Try JSON boundary first — prevents a \n from a later message
        // being incorrectly used to delimit a JSON message without trailing \n.
        if (TryGetCompleteJsonLength(data, offset, out int jsonLength))
        {
            int contentLen = jsonLength - offset;
            bool hasTrailingDelimiter = jsonLength < data.Length && data[jsonLength] == Delimiter;
            return (contentLen, hasTrailingDelimiter ? contentLen + 1 : contentLen);
        }

        // Fall back to newline delimiter for non-JSON messages.
        return data[offset..].IndexOf(Delimiter) switch
        {
            var i and >= 0 => (i, i + 1),
            _ => default
        };
    }

    private bool TryGetCompleteJsonLength(ReadOnlySpan<byte> span, int offset, out int messageLength)
    {
        messageLength = 0;
        bool resuming = _jsonParseState.IsActive;
        int startOffset = resuming ? _jsonParseState.StartOffset : GetStartingOffset(span, offset);

        // Validate saved state — buffer may have been reused by a non-accumulating caller
        if (resuming && (startOffset >= span.Length || !StartsWithObject(span[startOffset..])))
        {
            ResetJsonParseState();
            resuming = false;
            startOffset = GetStartingOffset(span, offset);
        }

        ReadOnlySpan<byte> json = span[startOffset..];

        if (resuming || StartsWithObject(json))
        {
            Utf8JsonReader reader = new(json[_jsonParseState.BytesConsumed..], isFinalBlock: false, _jsonParseState.ReaderState);
            try
            {
                while (reader.Read())
                {
                    if (IsEndOfJsonStructure(reader))
                    {
                        messageLength = startOffset + _jsonParseState.BytesConsumed + (int)reader.BytesConsumed;
                        return true;
                    }
                }

                SaveTemporaryState(reader);
            }
            catch (JsonException)
            {
                ResetJsonParseState();
            }
        }

        return false;

        static int GetStartingOffset(ReadOnlySpan<byte> span, int offset)
        {
            int i = span[offset..].IndexOfAnyExcept(Whitespace);
            return i < 0 ? span.Length : offset + i;
        }

        static bool StartsWithObject(ReadOnlySpan<byte> span) => !span.IsEmpty && span[0] is (byte)'{' or (byte)'[';

        bool IsEndOfJsonStructure(Utf8JsonReader reader) => reader.CurrentDepth == 0 && reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray;

        void SaveTemporaryState(Utf8JsonReader reader) => _jsonParseState = new(reader.CurrentState, _jsonParseState.BytesConsumed + (int)reader.BytesConsumed, startOffset);
    }

    private void ResetJsonParseState() => _jsonParseState = new();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _overflow.Dispose();
        }

        base.Dispose(disposing);
    }

    public ValueTask<int> WriteEndOfMessageAsync()
    {
        WriteByte(Delimiter);
        return ValueTask.FromResult(1);
    }

    private readonly record struct JsonParseState(JsonReaderState ReaderState = default, int BytesConsumed = 0, int StartOffset = -1)
    {
        public JsonParseState() : this(default, 0, -1) { }
        public bool IsActive => StartOffset >= 0;
    }

    private struct PooledBuffer()
    {
        private byte[] _data = [];
        private int _offset;
        private int _length;

        public int CopyTo(ArraySegment<byte> destination)
        {
            if (_length > 0)
            {
                int toCopy = Math.Min(_length, destination.Count);
                Buffer.BlockCopy(_data, _offset, destination.Array!, destination.Offset, toCopy);
                _offset += toCopy;
                _length -= toCopy;

                if (_length == 0)
                {
                    _offset = 0;
                }

                return toCopy;
            }

            return 0;
        }

        public void Append(ReadOnlySpan<byte> source)
        {
            if (!source.IsEmpty)
            {
                if (_length > 0 && _offset >= source.Length)
                {
                    _offset -= source.Length;
                    source.CopyTo(_data.AsSpan(_offset, source.Length));
                }
                else
                {
                    EnsureCapacity(_offset + _length + source.Length);
                    source.CopyTo(_data.AsSpan(_offset + _length));
                }

                _length += source.Length;
            }
        }

        private void EnsureCapacity(int required)
        {
            if (_data.Length < required)
            {
                byte[] newBuffer = ArrayPool<byte>.Shared.Rent(required);
                if (_length > 0)
                {
                    Buffer.BlockCopy(_data, _offset, newBuffer, 0, _length);
                }

                if (_data.Length != 0)
                {
                    ArrayPool<byte>.Shared.Return(_data);
                }

                _data = newBuffer;
                _offset = 0;
            }
        }

        public void Dispose()
        {
            if (_data.Length != 0)
            {
                ArrayPool<byte>.Shared.Return(_data);
                _data = [];
            }
        }
    }
}
