// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.JsonRpc.Hosting;

/// <summary>Owns a pooled, size-bounded HTTP JSON-RPC request body.</summary>
public sealed class JsonRpcRequestBody : IDisposable
{
    private const string MalformedRequestBodyMessage = "Invalid request body.";

    private const int DefaultInitialCapacity = 256;

    private byte[]? _buffer;
    private int _initialCapacity = DefaultInitialCapacity;

    /// <summary>The number of retained body bytes.</summary>
    public int BytesRead { get; private set; }

    /// <summary>The collected body, valid until disposal.</summary>
    public ReadOnlyMemory<byte> Memory =>
        _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, BytesRead);

    private void SetInitialCapacity(int initialCapacity)
    {
        if (_buffer is null && initialCapacity > _initialCapacity)
        {
            _initialCapacity = initialCapacity;
        }
    }

    private void EnsureExactCapacity(int capacity)
    {
        if (capacity <= 0 || _buffer?.Length >= capacity)
        {
            return;
        }

        RentCapacity(capacity);
    }

    private void EnsureCapacity(int minCapacity)
    {
        if (minCapacity <= 0 || _buffer?.Length >= minCapacity)
        {
            return;
        }

        int newCapacity = _buffer is null ? _initialCapacity : _buffer.Length;
        while (newCapacity < minCapacity)
        {
            newCapacity = newCapacity <= Array.MaxLength / 2 ? newCapacity * 2 : minCapacity;
        }

        RentCapacity(newCapacity);
    }

    private void RentCapacity(int newCapacity)
    {
        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
        if (_buffer is not null)
        {
            _buffer.AsSpan(0, BytesRead).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = newBuffer;
    }

    private void Append(ReadOnlySequence<byte> sequence)
    {
        int sequenceLength = (int)sequence.Length;
        if (sequenceLength == 0)
        {
            return;
        }

        int newLength = BytesRead + sequenceLength;
        EnsureCapacity(newLength);
        sequence.CopyTo(_buffer!.AsSpan(BytesRead, sequenceLength));
        BytesRead = newLength;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_buffer is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
        BytesRead = 0;
    }

    /// <summary>Collects one request body, checking its limit before retaining bytes.</summary>
    public async ValueTask ReadAsync(
        HttpContext context,
        long? contentLength,
        long? maxRequestBodySize,
        CancellationToken cancellationToken, Action<int>? onRead = null)
    {
        const int MissingContentLengthInitialCapacity = 4096;

        if (contentLength is > 0 &&
            maxRequestBodySize is not null &&
            contentLength.Value > maxRequestBodySize.Value)
        {
            ThrowRequestBodyTooLarge(maxRequestBodySize.Value);
        }

        if (contentLength is > 0 and <= int.MaxValue)
        {
            onRead?.Invoke((int)contentLength.Value);
            EnsureExactCapacity((int)contentLength.Value);
        }
        else if (contentLength is null or > int.MaxValue)
        {
            SetInitialCapacity(MissingContentLengthInitialCapacity);
        }

        PipeReader bodyReader = context.Request.BodyReader;
        try
        {
            while (true)
            {
                ReadResult readResult;
                try
                {
                    readResult = await bodyReader.ReadAsync(cancellationToken);
                }
                catch (IOException e) when (e.GetType() == typeof(IOException))
                {
                    // Kestrel reports some malformed bodies (e.g. a chunk-size line overflowing Int32) as a *bare*
                    // IOException; without this it escapes as an unhandled 500 instead of a framed 400.
                    //
                    // The exact-type test is deliberate. Everything Kestrel surfaces from the body pipe derives from
                    // IOException, including BadHttpRequestException (already handled below) and the genuine
                    // transport failures - ConnectionResetException, a mid-body TLS or stream error, an IOException
                    // wrapping a socket error. Reframing those as a client error would blame the caller for a
                    // dropped connection and, because the handler below logs at Debug, would leave a real I/O
                    // failure with no trace at default log level. They keep propagating instead.
                    //
                    // The message is a constant on purpose, even though the handler below no longer echoes an
                    // arbitrary one: the real transport-layer message stays on the inner exception, which
                    // LogBadRequest writes at Debug.
                    throw new Microsoft.AspNetCore.Http.BadHttpRequestException(MalformedRequestBodyMessage, StatusCodes.Status400BadRequest, e);
                }

                ReadOnlySequence<byte> buffer = readResult.Buffer;

                long newBytesRead = BytesRead + buffer.Length;
                if (maxRequestBodySize is not null && newBytesRead > maxRequestBodySize)
                {
                    ThrowRequestBodyTooLarge(maxRequestBodySize.Value);
                }

                if (newBytesRead > int.MaxValue)
                {
                    ThrowRequestBodyTooLarge(maxRequestBodySize ?? int.MaxValue);
                }

                onRead?.Invoke((int)newBytesRead);
                Append(buffer);
                bodyReader.AdvanceTo(buffer.End);

                if (readResult.IsCompleted || readResult.IsCanceled)
                {
                    break;
                }
            }
        }
        finally
        {
            await bodyReader.CompleteAsync();
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowRequestBodyTooLarge(long maxRequestBodySize) =>
            throw new Microsoft.AspNetCore.Http.BadHttpRequestException(
                $"Request body too large. The max request body size is {maxRequestBodySize} bytes.",
                StatusCodes.Status413PayloadTooLarge);
    }

}
